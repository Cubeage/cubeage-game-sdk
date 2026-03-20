-- cubeage_sdk_config.lua
-- Configuration for the Cubeage Game SDK v2.1.0.
-- Edit this file before shipping your game.

local config = {
    -- Base URL of the Cubeage Platform backend (no trailing slash)
    apiBaseUrl = "https://api.cubeage.com",

    -- Unique key identifying your game.
    -- Format: {game}_{platform}  e.g. "fun_showhand_ios", "fun_mahjong_android"
    appKey = "fun_game_ios",

    -- SDK version string (do not change)
    sdkVersion = "2.1.0",

    -- HTTP timeout in milliseconds
    timeoutMs = 15000,

    -- Print SDK debug logs to the console (disable in production builds)
    verboseLogging = false,

    -- Legacy migration: path to old SDK data file (relative to DocumentsDirectory).
    -- The file should contain the raw old device ID / token as plain text.
    -- Leave as empty string ("") to skip migration entirely.
    -- Example: "gameflask_device.dat"
    legacyTokenFile = "",
}

return config
