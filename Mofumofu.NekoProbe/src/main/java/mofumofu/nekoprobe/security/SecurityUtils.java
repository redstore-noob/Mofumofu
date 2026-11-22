package mofumofu.nekoprobe.security;

import mofumofu.nekoprobe.api.ApiException;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.util.HexFormat;

public final class SecurityUtils {
    private static final SecureRandom RANDOM = new SecureRandom();
    private static final HexFormat HEX = HexFormat.of();

    private SecurityUtils() {
    }

    public static String randomHex(int bytes) {
        byte[] value = new byte[bytes];
        RANDOM.nextBytes(value);
        return HEX.formatHex(value);
    }

    public static String newToken() {
        return randomHex(32);
    }

    public static byte[] sha256(byte[] value) {
        try {
            return MessageDigest.getInstance("SHA-256").digest(value);
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }

    public static String sha256Hex(byte[] value) {
        return HEX.formatHex(sha256(value));
    }

    public static String sha256Hex(String value) {
        return sha256Hex(value.getBytes(StandardCharsets.UTF_8));
    }

    public static boolean fixedTimeEquals(String leftHex, String rightHex) {
        if (!isLowerHex(leftHex, 64) || !isLowerHex(rightHex, 64)) {
            return false;
        }
        return MessageDigest.isEqual(HEX.parseHex(leftHex), HEX.parseHex(rightHex));
    }

    public static boolean isToken(String token) {
        return isLowerHex(token, 64);
    }

    public static boolean isLowerHex(String value, int length) {
        if (value == null || value.length() != length) {
            return false;
        }
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) {
                return false;
            }
        }
        return true;
    }

    public static String validPlayerName(String value) {
        if (value == null || value.isEmpty() || value.length() > 16) {
            throw new ApiException(400, "invalid_player_name", "Player name must contain 1 to 16 letters, digits, or underscores.");
        }
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_')) {
                throw new ApiException(400, "invalid_player_name", "Player name must contain 1 to 16 letters, digits, or underscores.");
            }
        }
        return value;
    }

    public static String validCommand(String value) {
        if (value == null || value.isBlank() || value.length() > 256) {
            throw new ApiException(400, "invalid_command", "Command must contain 1 to 256 non-control characters.");
        }
        for (int i = 0; i < value.length(); i++) {
            if (Character.isISOControl(value.charAt(i))) {
                throw new ApiException(400, "invalid_command", "Command must not contain CR, LF, NUL, or other control characters.");
            }
        }
        return value;
    }

    public static String requireText(JsonTextAccessor accessor) {
        return accessor.get();
    }

    @FunctionalInterface
    public interface JsonTextAccessor {
        String get();
    }
}
