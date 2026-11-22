package mofumofu.nekoprobe.bukkit;

import org.apache.logging.log4j.LogManager;
import org.apache.logging.log4j.core.Appender;
import org.apache.logging.log4j.core.Core;
import org.apache.logging.log4j.core.Filter;
import org.apache.logging.log4j.core.LogEvent;
import org.apache.logging.log4j.core.LoggerContext;
import org.apache.logging.log4j.core.appender.AbstractAppender;
import org.apache.logging.log4j.core.config.Configuration;
import org.apache.logging.log4j.core.config.LoggerConfig;
import org.apache.logging.log4j.core.config.plugins.Plugin;

@Plugin(name = "NekoProbeCapture", category = Core.CATEGORY_NAME, elementType = Appender.ELEMENT_TYPE)
public final class ConsoleCapture extends AbstractAppender {
    private final ConsoleRing ring;

    private ConsoleCapture(ConsoleRing ring) {
        super("NekoProbeCapture", null, null, true, null);
        this.ring = ring;
    }

    @Override
    public void append(LogEvent event) {
        ring.appendConsole(event.getLevel().name(), event.getLoggerName(), event.getMessage().getFormattedMessage());
    }

    public static ConsoleCapture attach(ConsoleRing ring) {
        ConsoleCapture capture = new ConsoleCapture(ring);
        capture.start();
        LoggerContext context = (LoggerContext) LogManager.getContext(false);
        Configuration configuration = context.getConfiguration();
        configuration.addAppender(capture);
        LoggerConfig root = configuration.getRootLogger();
        root.addAppender(capture, null, null);
        context.updateLoggers();
        return capture;
    }

    public void detach() {
        LoggerContext context = (LoggerContext) LogManager.getContext(false);
        Configuration configuration = context.getConfiguration();
        configuration.getRootLogger().removeAppender(getName());
        configuration.getAppenders().remove(getName());
        context.updateLoggers();
        stop();
    }
}
