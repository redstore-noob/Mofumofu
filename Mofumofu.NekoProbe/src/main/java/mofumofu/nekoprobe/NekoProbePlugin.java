package mofumofu.nekoprobe;

import mofumofu.nekoprobe.backup.BackupService;
import mofumofu.nekoprobe.backup.RestoreService;
import mofumofu.nekoprobe.bukkit.BukkitGateway;
import mofumofu.nekoprobe.bukkit.ConsoleCapture;
import mofumofu.nekoprobe.bukkit.ConsoleRing;
import mofumofu.nekoprobe.bukkit.EventService;
import mofumofu.nekoprobe.bukkit.MainThread;
import mofumofu.nekoprobe.bukkit.OperationRegistry;
import mofumofu.nekoprobe.bukkit.PlayerEventsListener;
import mofumofu.nekoprobe.files.PropertiesService;
import mofumofu.nekoprobe.files.SafeFileService;
import mofumofu.nekoprobe.http.ProbeHttpService;
import mofumofu.nekoprobe.http.ProbeRouter;
import mofumofu.nekoprobe.security.AgentEventClient;
import mofumofu.nekoprobe.security.AuditLog;
import mofumofu.nekoprobe.security.ConfirmationService;
import mofumofu.nekoprobe.security.TokenRateLimiter;
import mofumofu.nekoprobe.security.TokenService;
import org.bukkit.Bukkit;
import org.bukkit.plugin.java.JavaPlugin;

import java.nio.file.Path;
import java.time.Clock;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.atomic.AtomicInteger;

public final class NekoProbePlugin extends JavaPlugin {
    private final Clock clock = Clock.systemUTC();
    private final long startedNanos = System.nanoTime();
    private Path serverRoot;
    private ProbeConfig probeConfig;
    private ExecutorService executor;
    private ProbeHttpService httpService;
    private ConsoleCapture consoleCapture;
    private boolean restoreFailed;

    @Override
    public void onLoad() {
        serverRoot = Path.of("").toAbsolutePath().normalize();
        try {
            saveDefaultConfig();
            probeConfig = ProbeConfig.load(serverRoot, getDataFolder().toPath());
            BackupService backups = new BackupService(serverRoot, probeConfig.stateDirectory(), clock, null);
            RestoreService restore = new RestoreService(serverRoot, probeConfig.stateDirectory(), backups);
            if (restore.hasRestoreActivity()) {
                restore.restorePending();
            }
        } catch (Exception e) {
            restoreFailed = true;
            getLogger().severe("NekoProbe restore or startup validation failed; server startup is being aborted.");
            Bukkit.getServer().shutdown();
        }
    }

    @Override
    public void onEnable() {
        if (restoreFailed) {
            Bukkit.getServer().shutdown();
            return;
        }
        try {
            executor = Executors.newFixedThreadPool(4, new DaemonThreadFactory());
            TokenService tokenService = new TokenService(probeConfig.stateDirectory(), probeConfig.instanceId(), clock);
            tokenService.initialize(probeConfig.publicUrl(), probeConfig.port());

            ConsoleRing journal = new ConsoleRing(2048, clock);
            consoleCapture = ConsoleCapture.attach(journal);
            MainThread mainThread = new MainThread(this);
            BukkitGateway gateway = new BukkitGateway(mainThread);
            gateway.initializeOnlinePlayers();
            OperationRegistry operations = new OperationRegistry(clock, journal);
            AgentEventClient agentClient = probeConfig.agentCallback() == null ? null
                    : new AgentEventClient(probeConfig.agentCallback(), probeConfig.instanceId(), tokenService.linkSecret(), clock, executor);
            EventService events = new EventService(clock, journal, agentClient);
            Bukkit.getPluginManager().registerEvents(new PlayerEventsListener(gateway, events), this);

            SafeFileService files = new SafeFileService(serverRoot, probeConfig.stateDirectory());
            PropertiesService properties = new PropertiesService(serverRoot);
            BackupService backups = new BackupService(serverRoot, probeConfig.stateDirectory(), clock, gateway);
            ProbeRouter router = new ProbeRouter(probeConfig, getDescription().getVersion(), clock, startedNanos,
                    tokenService, new TokenRateLimiter(30, clock), new AuditLog(probeConfig.stateDirectory(), clock),
                    new ConfirmationService(probeConfig.instanceId(), clock), gateway, files, properties, backups,
                    operations, journal, events);
            httpService = new ProbeHttpService(probeConfig, router, tokenService, journal, clock, executor,
                    message -> getLogger().warning(message));
            httpService.start();
            events.publish("server.state_changed", "NekoProbe is online", java.util.Map.of("state", "running"));
            getLogger().info("NekoProbe is listening on 127.0.0.1:" + probeConfig.port());
        } catch (Exception e) {
            getLogger().severe("NekoProbe could not start safely (" + e.getClass().getSimpleName() + "); disabling the probe.");
            cleanup();
            Bukkit.getPluginManager().disablePlugin(this);
        }
    }

    @Override
    public void onDisable() {
        cleanup();
    }

    private void cleanup() {
        if (httpService != null) {
            try {
                httpService.stop();
            } catch (RuntimeException ignored) {
                // Continue cleanup.
            }
            httpService = null;
        }
        if (consoleCapture != null) {
            try {
                consoleCapture.detach();
            } catch (RuntimeException ignored) {
                // Continue cleanup.
            }
            consoleCapture = null;
        }
        if (executor != null) {
            executor.shutdownNow();
            executor = null;
        }
    }

    private static final class DaemonThreadFactory implements ThreadFactory {
        private final AtomicInteger sequence = new AtomicInteger();

        @Override
        public Thread newThread(Runnable runnable) {
            Thread thread = new Thread(runnable, "NekoProbe-IO-" + sequence.incrementAndGet());
            thread.setDaemon(true);
            return thread;
        }
    }
}
