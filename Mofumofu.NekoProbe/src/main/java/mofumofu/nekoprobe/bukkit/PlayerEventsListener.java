package mofumofu.nekoprobe.bukkit;

import org.bukkit.event.EventHandler;
import org.bukkit.event.Listener;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.event.player.PlayerQuitEvent;

import java.util.Map;

public final class PlayerEventsListener implements Listener {
    private final BukkitGateway gateway;
    private final EventService events;

    public PlayerEventsListener(BukkitGateway gateway, EventService events) {
        this.gateway = gateway;
        this.events = events;
    }

    @EventHandler
    public void onJoin(PlayerJoinEvent event) {
        gateway.joined(event.getPlayer());
        events.publish("player.joined", event.getPlayer().getName() + " joined", Map.of(
                "playerName", event.getPlayer().getName(), "uuid", event.getPlayer().getUniqueId().toString()));
    }

    @EventHandler
    public void onQuit(PlayerQuitEvent event) {
        gateway.quit(event.getPlayer());
        events.publish("player.left", event.getPlayer().getName() + " left", Map.of(
                "playerName", event.getPlayer().getName(), "uuid", event.getPlayer().getUniqueId().toString()));
    }
}
