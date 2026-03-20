// CubeageSDKConfig.ts — edit before shipping
// Import this into your main scene and pass to CubeageSDK.instance.init(config)

import type { SDKConfig } from './CubeageSDK';

const config: SDKConfig = {
    apiBaseUrl:      "https://api.cubeage.com",
    gameSlug:        "blackjack",       // your game slug
    legacyTokenKey:  "deviceId",        // key in cc.sys.localStorage for old device ID
    verboseLogging:  false,
};

export default config;
