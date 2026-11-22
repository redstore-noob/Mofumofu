package mofumofu.nekoprobe.bukkit;

import mofumofu.nekoprobe.backup.BackupService;

import java.time.Instant;
import java.util.Map;

public interface ServerGateway extends BackupService.BackupControl {
    Map<String, Object> status();

    Map<String, Object> serverInfo();

    Map<String, Object> onlinePlayers();

    boolean dispatchCommand(String command);

    Map<String, Object> playerList(String kind);

    Map<String, Object> addPlayer(String kind, String playerName, String expectedRevision, Integer opLevel,
                                  Boolean bypassesPlayerLimit, String reason, Instant expiresAt);

    Map<String, Object> removePlayer(String kind, String playerName, String expectedRevision);
}
