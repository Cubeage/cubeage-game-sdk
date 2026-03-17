-- cubeage_sdk_config.lua
-- Configuration for the Cubeage Game SDK.
-- Edit this file before shipping your game.

local config = {
    -- Base URL of the Cubeage Platform backend (no trailing slash)
    apiBaseUrl = "https://api.cubeage.com",

    -- Unique key identifying your game + platform.
    -- Format: {game}_{platform}  e.g. "fun_showhand_ios", "fun_mahjong_android"
    appKey = "fun_game_ios",

    -- SDK version string (do not change)
    sdkVersion = "1.0.0",

    -- HTTP timeout in milliseconds
    timeoutMs = 15000,

    -- Print SDK debug logs to the console (disable in production builds)
    verboseLogging = true,
}

return config
