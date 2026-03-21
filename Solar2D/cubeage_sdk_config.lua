-- cubeage_sdk_config.lua
-- Template configuration for Solar2D games using Cubeage SDK v2.1.0
-- Copy to your game's client/src/ and fill in appKey

local platform = system.getInfo("platform")
local appKey
if platform == "android" then
    appKey = "your-app-key"  -- Replace with your app key
else
    appKey = "your-app-key"  -- Replace with your app key
end

local config = {
    apiBaseUrl   = "https://new.cubeage.com",
    appKey       = appKey,
    sdkVersion   = "2.1.0",
    verboseLogging = false,
    legacyTokenFile = "",
}

return config
