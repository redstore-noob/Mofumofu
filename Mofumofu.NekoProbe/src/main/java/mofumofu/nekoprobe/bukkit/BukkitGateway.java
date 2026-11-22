package mofumofu.nekoprobe.bukkit;

import mofumofu.nekoprobe.api.ApiException;
import mofumofu.nekoprobe.api.Json;
import mofumofu.nekoprobe.security.SecurityUtils;
import org.bukkit.BanList;
import org.bukkit.Bukkit;
import org.bukkit.OfflinePlayer;
import org.bukkit.entity.Player;

import java.lang.management.ManagementFactory;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.Date;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

public final class BukkitGateway implements ServerGateway {
    private final MainThread mainThread;
    private final Map<UUID, Instant> joinedAt = new ConcurrentHashMap<>();

    public BukkitGateway(MainThread mainThread) {
        this.mainThread = mainThread;
    }

    public void initializeOnlinePlayers() {
        mainThread.run(() -> Bukkit.getOnlinePlayers().forEach(player -> joinedAt.put(player.getUniqueId(), Instant.now())));
    }

    public void joined(Player player) {
        joinedAt.put(player.getUniqueId(), Instant.now());
    }

    public void quit(Player player) {
        joinedAt.remove(player.getUniqueId());
    }

    @Override
    public Map<String, Object> status() {
        return mainThread.call(() -> {
            Runtime runtime = Runtime.getRuntime();
            Map<String, Object> status = new LinkedHashMap<>();
            status.put("state", "running");
            status.put("onlinePlayers", Bukkit.getOnlinePlayers().size());
            status.put("maxPlayers", Bukkit.getMaxPlayers());
            status.put("cpuPercent", processCpu());
            status.put("memoryUsedBytes", runtime.totalMemory() - runtime.freeMemory());
            status.put("memoryMaxBytes", runtime.maxMemory());
            status.put("tps", null);
            status.put("minecraftVersion", Bukkit.getMinecraftVersion());
            status.put("motd", Bukkit.getMotd());
            return status;
        });
    }

    @Override
    public Map<String, Object> serverInfo() {
        return mainThread.call(() -> {
            Map<String, Object> info = new LinkedHashMap<>();
            info.put("serverName", Bukkit.getName());
            info.put("serverImplementationVersion", Bukkit.getName() + " " + Bukkit.getVersion());
            info.put("minecraftVersion", Bukkit.getMinecraftVersion());
            return info;
        });
    }

    @Override
    public Map<String, Object> onlinePlayers() {
        return mainThread.call(() -> {
            List<Map<String, Object>> players = new ArrayList<>();
            Instant now = Instant.now();
            for (Player player : Bukkit.getOnlinePlayers().stream().sorted(Comparator.comparing(Player::getName)).toList()) {
                Instant joined = joinedAt.getOrDefault(player.getUniqueId(), now);
                Map<String, Object> value = new LinkedHashMap<>();
                value.put("name", player.getName());
                value.put("uuid", player.getUniqueId());
                value.put("joinedAt", joined);
                value.put("onlineSeconds", Math.max(0, now.getEpochSecond() - joined.getEpochSecond()));
                value.put("pingMilliseconds", player.getPing());
                value.put("gameMode", player.getGameMode().name().toLowerCase(Locale.ROOT));
                players.add(value);
            }
            Map<String, Object> response = new LinkedHashMap<>();
            response.put("onlineCount", players.size());
            response.put("maxPlayers", Bukkit.getMaxPlayers());
            response.put("players", players);
            return response;
        });
    }

    @Override
    public boolean dispatchCommand(String command) {
        String normalized = command.startsWith("/") ? command.substring(1) : command;
        return mainThread.call(() -> Bukkit.dispatchCommand(Bukkit.getConsoleSender(), normalized));
    }

    @Override
    public void saveOff() {
        dispatchRequired("save-off");
    }

    @Override
    public void saveAllFlush() {
        dispatchRequired("save-all flush");
    }

    @Override
    public void saveOn() {
        dispatchRequired("save-on");
    }

    private void dispatchRequired(String command) {
        if (!dispatchCommand(command)) {
            throw new ApiException(503, "server_unavailable", "The server rejected a required save command.");
        }
    }

    @Override
    public Map<String, Object> playerList(String kind) {
        return mainThread.call(() -> playerListOnMain(kind));
    }

    private Map<String, Object> playerListOnMain(String kind) {
        List<Map<String, Object>> entries = new ArrayList<>();
        switch (kind) {
            case "whitelist" -> {
                for (OfflinePlayer player : Bukkit.getWhitelistedPlayers()) {
                    Map<String, Object> entry = offlineEntry(player);
                    entries.add(entry);
                }
            }
            case "ops" -> {
                for (OfflinePlayer player : Bukkit.getOperators()) {
                    entries.add(offlineEntry(player));
                }
            }
            case "bans" -> {
                BanList bans = Bukkit.getBanList(BanList.Type.NAME);
                for (Object value : bans.getEntries()) {
                    org.bukkit.BanEntry<?> ban = (org.bukkit.BanEntry<?>) value;
                    Map<String, Object> entry = new LinkedHashMap<>();
                    entry.put("name", ban.getTarget());
                    entry.put("uuid", null);
                    entry.put("reason", ban.getReason());
                    entry.put("source", ban.getSource());
                    entry.put("createdAt", instant(ban.getCreated()));
                    entry.put("expiresAt", instant(ban.getExpiration()));
                    entries.add(entry);
                }
            }
            default -> throw ApiException.notFound("Player list");
        }
        entries.sort(Comparator.comparing(entry -> entry.get("name") == null ? "" : entry.get("name").toString()));
        Map<String, Object> response = new LinkedHashMap<>();
        response.put("kind", kind);
        response.put("revision", revision(entries));
        response.put("entries", entries);
        return response;
    }

    @Override
    public Map<String, Object> addPlayer(String kind, String playerName, String expectedRevision, Integer opLevel,
                                         Boolean bypassesPlayerLimit, String reason, Instant expiresAt) {
        SecurityUtils.validPlayerName(playerName);
        if (reason != null && (reason.length() > 512 || reason.chars().anyMatch(Character::isISOControl))) {
            throw ApiException.badRequest("Ban reason is invalid.");
        }
        return mainThread.call(() -> {
            checkRevision(kind, expectedRevision);
            switch (kind) {
                case "whitelist" -> Bukkit.getOfflinePlayer(playerName).setWhitelisted(true);
                case "ops" -> Bukkit.getOfflinePlayer(playerName).setOp(true);
                case "bans" -> Bukkit.getBanList(BanList.Type.NAME).addBan(playerName, reason,
                        expiresAt == null ? null : Date.from(expiresAt), "NekoProbe");
                default -> throw ApiException.notFound("Player list");
            }
            if (kind.equals("whitelist")) {
                Bukkit.reloadWhitelist();
            }
            Map<String, Object> result = mutationResult(kind, playerName, false);
            return result;
        });
    }

    @Override
    public Map<String, Object> removePlayer(String kind, String playerName, String expectedRevision) {
        SecurityUtils.validPlayerName(playerName);
        return mainThread.call(() -> {
            checkRevision(kind, expectedRevision);
            switch (kind) {
                case "whitelist" -> {
                    Bukkit.getOfflinePlayer(playerName).setWhitelisted(false);
                    Bukkit.reloadWhitelist();
                }
                case "ops" -> Bukkit.getOfflinePlayer(playerName).setOp(false);
                case "bans" -> Bukkit.getBanList(BanList.Type.NAME).pardon(playerName);
                default -> throw ApiException.notFound("Player list");
            }
            return mutationResult(kind, playerName, true);
        });
    }

    private Map<String, Object> mutationResult(String kind, String playerName, boolean removed) {
        Map<String, Object> list = playerListOnMain(kind);
        Map<String, Object> entry = listEntries(list).stream()
                .filter(value -> playerName.equalsIgnoreCase(String.valueOf(value.get("name")))).findFirst()
                .orElseGet(() -> {
                    Map<String, Object> missing = new LinkedHashMap<>();
                    missing.put("name", playerName);
                    return missing;
                });
        Map<String, Object> response = new LinkedHashMap<>();
        response.put("kind", kind);
        response.put("revision", list.get("revision"));
        response.put("entry", entry);
        response.put("removed", removed);
        return response;
    }

    @SuppressWarnings("unchecked")
    private static List<Map<String, Object>> listEntries(Map<String, Object> list) {
        return (List<Map<String, Object>>) list.get("entries");
    }

    private void checkRevision(String kind, String expectedRevision) {
        String actual = playerListOnMain(kind).get("revision").toString();
        if (expectedRevision == null || !SecurityUtils.fixedTimeEquals(expectedRevision.toLowerCase(Locale.ROOT), actual)) {
            throw ApiException.conflict("revision_conflict", "The player list has changed; reload it before modifying entries.");
        }
    }

    private static Map<String, Object> offlineEntry(OfflinePlayer player) {
        Map<String, Object> entry = new LinkedHashMap<>();
        entry.put("name", player.getName());
        entry.put("uuid", player.getUniqueId());
        return entry;
    }

    private static String revision(List<Map<String, Object>> entries) {
        return SecurityUtils.sha256Hex(Json.write(entries));
    }

    private static Instant instant(Date date) {
        return date == null ? null : date.toInstant();
    }

    private static Double processCpu() {
        if (ManagementFactory.getOperatingSystemMXBean() instanceof com.sun.management.OperatingSystemMXBean bean) {
            double load = bean.getProcessCpuLoad();
            return Double.isNaN(load) || load < 0 ? null : load * 100.0;
        }
        return null;
    }
}
