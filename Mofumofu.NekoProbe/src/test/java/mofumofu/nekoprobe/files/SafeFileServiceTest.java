package mofumofu.nekoprobe.files;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.nio.file.Path;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

public final class SafeFileServiceTest {
    @TempDir
    Path root;

    private SafeFileService service() {
        return new SafeFileService(root, root.resolve("state"));
    }

    @Test
    void createFileReturnsProtocolMutationShape() {
        Map<String, Object> created = service().create("config.txt", false, "hello");

        assertEquals("config.txt", created.get("path"));
        assertEquals(false, created.get("isDirectory"));
        assertTrue(created.get("revision") instanceof String revision && revision.length() == 64);
        assertNotNull(created.get("modifiedAt"));
    }

    @Test
    void createDirectoryReturnsDirectoryRevision() {
        Map<String, Object> created = service().create("config", true, null);

        assertEquals("config", created.get("path"));
        assertEquals(true, created.get("isDirectory"));
        assertTrue(created.get("revision").toString().startsWith("d_"));
    }

    @Test
    void renameReportsDestinationAsCurrentPath() {
        SafeFileService files = service();
        files.create("config", true, null);
        Map<String, Object> file = files.create("config/old.txt", false, "value");

        Map<String, Object> renamed = files.rename("config/old.txt", "config/new.txt",
                (String) file.get("revision"));

        assertEquals("config/new.txt", renamed.get("path"));
        assertEquals("config/new.txt", renamed.get("newPath"));
        assertEquals(false, renamed.get("isDirectory"));
    }

    @Test
    void renameDirectoryKeepsDirectoryRevision() {
        SafeFileService files = service();
        files.create("config", true, null);
        files.create("config/old", true, null);
        files.create("config/old/settings.txt", false, "value");

        Map<String, Object> renamed = files.rename("config/old", "config/new", null);

        assertEquals("config/new", renamed.get("path"));
        assertEquals(true, renamed.get("isDirectory"));
        assertTrue(renamed.get("revision").toString().startsWith("d_"));
    }
}
