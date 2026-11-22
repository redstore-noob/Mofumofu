package mofumofu.nekoprobe.bukkit;

import mofumofu.nekoprobe.api.ApiException;
import org.bukkit.Bukkit;
import org.bukkit.plugin.Plugin;

import java.util.concurrent.Callable;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.function.Supplier;

public final class MainThread {
    private final Plugin plugin;

    public MainThread(Plugin plugin) {
        this.plugin = plugin;
    }

    public <T> T call(Supplier<T> supplier) {
        if (Bukkit.isPrimaryThread()) {
            return supplier.get();
        }
        Callable<T> task = supplier::get;
        try {
            return Bukkit.getScheduler().callSyncMethod(plugin, task).get(30, TimeUnit.SECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new ApiException(503, "server_unavailable", "The server main thread is unavailable.");
        } catch (ExecutionException e) {
            Throwable cause = e.getCause();
            if (cause instanceof ApiException apiException) {
                throw apiException;
            }
            if (cause instanceof RuntimeException runtimeException) {
                throw runtimeException;
            }
            throw ApiException.internal();
        } catch (TimeoutException e) {
            throw new ApiException(503, "server_unavailable", "The server main thread did not respond in time.");
        }
    }

    public void run(Runnable runnable) {
        call(() -> {
            runnable.run();
            return null;
        });
    }
}
