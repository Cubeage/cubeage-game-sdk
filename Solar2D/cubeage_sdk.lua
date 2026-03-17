--------------------------------------------------------------------------------
-- Cubeage Game SDK for Solar2D — Commercial Grade
--
-- Features:
--   - Anonymous auth with Bearer token management
--   - Automatic session management (foreground/background)
--   - Event tracking with batch support
--   - Ad revenue tracking (LevelPlay ARM)
--   - Install attribution (Singular S2S)
--   - User properties
--   - Force update checks
--   - Offline queue with file persistence
--   - Retry with exponential backoff
--
-- Usage:
--   local cubeageSDK = require("cubeage_sdk")
--   cubeageSDK.init({ apiBaseUrl = "...", appKey = "..." }, function(config)
--       print("SDK ready!")
--   end)
--------------------------------------------------------------------------------

local json = require("json")
local network = require("network") -- Solar2D built-in
local lfs = require("lfs")

local M = {}

-- ─── Internal State ─────────────────────────────────────────────────────────

local _apiBaseUrl     = ""
local _appKey         = ""
local _sdkVersion     = "2.0.0"
local _verbose        = false

local _deviceId       = ""
local _accessToken    = ""
local _refreshToken   = ""
local _userId         = ""
local _initialized    = false
local _sessionId      = nil
local _sessionActive  = false
local _config         = {}

local _offlineQueue   = {}
local _isFlushing     = false
local _initCallback   = nil
local _initErrorCb    = nil

local MAX_RETRIES     = 3
local BASE_RETRY_MS   = 1000
local MAX_QUEUE_SIZE  = 500
local QUEUE_FILENAME  = "cubeage_sdk_queue.json"
local PREFS_FILENAME  = "cubeage_sdk_prefs.json"

-- ─── Persistence Helpers ────────────────────────────────────────────────────

local function getFilePath(filename)
    return system.pathForFile(filename, system.DocumentsDirectory)
end

local function saveJson(filename, data)
    local path = getFilePath(filename)
    if not path then return end
    local file = io.open(path, "w")
    if file then
        file:write(json.encode(data))
        file:close()
    end
end

local function loadJson(filename)
    local path = getFilePath(filename)
    if not path then return nil end
    local file = io.open(path, "r")
    if not file then return nil end
    local content = file:read("*a")
    file:close()
    if content and #content > 0 then
        local ok, data = pcall(json.decode, content)
        if ok then return data end
    end
    return nil
end

local function savePrefs()
    saveJson(PREFS_FILENAME, {
        deviceId     = _deviceId,
        accessToken  = _accessToken,
        refreshToken = _refreshToken,
        userId       = _userId,
        firstLaunch  = false,
    })
end

local function loadPrefs()
    return loadJson(PREFS_FILENAME)
end

local function saveQueue()
    saveJson(QUEUE_FILENAME, _offlineQueue)
end

local function loadQueue()
    local data = loadJson(QUEUE_FILENAME)
    if type(data) == "table" then
        _offlineQueue = data
    end
end

-- ─── Logging ────────────────────────────────────────────────────────────────

local function log(msg)
    if _verbose then print("[CubeageSDK] " .. tostring(msg)) end
end

local function logWarn(msg)
    print("[CubeageSDK] WARNING: " .. tostring(msg))
end

-- ─── Device ID ──────────────────────────────────────────────────────────────

local function resolveDeviceId()
    local prefs = loadPrefs()
    if prefs and prefs.deviceId and #prefs.deviceId > 0 then
        return prefs.deviceId
    end

    -- Use system device ID if available
    local id = system.getInfo("deviceID")
    if id and #id > 0 and id ~= "unknown" then return id end

    -- Generate a UUID fallback
    local template = "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx"
    id = string.gsub(template, "[xy]", function(c)
        local v = (c == "x") and math.random(0, 15) or math.random(8, 11)
        return string.format("%x", v)
    end)
    return id
end

-- ─── Platform Info ──────────────────────────────────────────────────────────

local function getPlatform()
    local p = system.getInfo("platform")
    if p == "ios" then return "ios" end
    if p == "android" then return "android" end
    return p
end

local function getDeviceModel()
    return system.getInfo("model") or "unknown"
end

local function getOSVersion()
    return system.getInfo("platformVersion") or "unknown"
end

local function getLocale()
    local lang = system.getPreference("ui", "language") or "en"
    local country = system.getPreference("locale", "country") or ""
    if country and #country > 0 then
        return lang .. "-" .. country
    end
    return lang
end

local function getTimezone()
    -- Solar2D doesn't have a direct timezone API; use offset
    local utcdate = os.date("!*t")
    local localdate = os.date("*t")
    local diff = (localdate.hour - utcdate.hour)
    return "UTC" .. (diff >= 0 and "+" or "") .. tostring(diff)
end

-- ─── HTTP Helpers ───────────────────────────────────────────────────────────

local function isOnline()
    -- Solar2D doesn't have a direct connectivity check;
    -- we rely on network.request failure as offline indicator
    return true
end

local function makeHeaders(withAuth)
    local headers = { ["Content-Type"] = "application/json" }
    if withAuth and #_accessToken > 0 then
        headers["Authorization"] = "Bearer " .. _accessToken
    end
    return headers
end

local function httpRequest(method, path, body, onSuccess, onError, retryCount)
    retryCount = retryCount or 0
    local url = _apiBaseUrl .. path
    log(method .. " " .. url)

    local params = {
        headers = makeHeaders(true),
        body    = body,
        timeout = 15,
    }

    network.request(url, method, function(event)
        if event.isError then
            if retryCount < MAX_RETRIES then
                local delay = BASE_RETRY_MS * math.pow(2, retryCount)
                log("Retrying " .. path .. " in " .. delay .. "ms (attempt " .. (retryCount + 1) .. ")")
                timer.performWithDelay(delay, function()
                    httpRequest(method, path, body, onSuccess, onError, retryCount + 1)
                end)
            else
                logWarn(method .. " " .. path .. " failed: " .. tostring(event.response))
                if onError then onError(event.response or "Network error") end
            end
            return
        end

        local status = event.status
        if status == 401 and retryCount == 0 then
            -- Token expired — try refresh
            refreshToken(function()
                httpRequest(method, path, body, onSuccess, onError, 1)
            end, function()
                if onError then onError("Authentication expired") end
            end)
            return
        end

        if status >= 200 and status < 300 then
            local data = nil
            if event.response and #event.response > 0 then
                local ok, parsed = pcall(json.decode, event.response)
                if ok then data = parsed end
            end
            if onSuccess then onSuccess(data, event.response) end
        elseif status >= 500 or status == 429 then
            -- Server error — retry
            if retryCount < MAX_RETRIES then
                local delay = BASE_RETRY_MS * math.pow(2, retryCount)
                timer.performWithDelay(delay, function()
                    httpRequest(method, path, body, onSuccess, onError, retryCount + 1)
                end)
            else
                if onError then onError("HTTP " .. tostring(status)) end
            end
        else
            logWarn(method .. " " .. path .. " HTTP " .. tostring(status))
            if onError then onError("HTTP " .. tostring(status)) end
        end
    end, params)
end

-- ─── Auth ───────────────────────────────────────────────────────────────────

local function anonymousAuth(onSuccess, onError)
    local body = json.encode({
        deviceId = _deviceId,
        appKey   = _appKey,
    })
    local url = _apiBaseUrl .. "/api/v1/auth/anonymous"
    log("POST " .. url)

    network.request(url, "POST", function(event)
        if event.isError or event.status ~= 200 then
            logWarn("Auth failed: " .. tostring(event.response))
            if onError then onError("Auth failed") end
            return
        end

        local ok, data = pcall(json.decode, event.response)
        if ok and data and data.accessToken then
            _accessToken  = data.accessToken
            _refreshToken = data.refreshToken or ""
            _userId       = data.userId or ""
            savePrefs()
            log("Authenticated: " .. _userId)
            if onSuccess then onSuccess() end
        else
            if onError then onError("Invalid auth response") end
        end
    end, { headers = { ["Content-Type"] = "application/json" }, body = body, timeout = 15 })
end

function refreshToken(onSuccess, onError)
    if not _refreshToken or #_refreshToken == 0 then
        -- No refresh token — re-auth
        anonymousAuth(onSuccess, onError)
        return
    end

    local body = json.encode({ refreshToken = _refreshToken })
    local url = _apiBaseUrl .. "/api/v1/auth/refresh"

    network.request(url, "POST", function(event)
        if event.isError or event.status ~= 200 then
            -- Refresh failed — re-auth
            _accessToken = ""
            _refreshToken = ""
            anonymousAuth(onSuccess, onError)
            return
        end

        local ok, data = pcall(json.decode, event.response)
        if ok and data and data.accessToken then
            _accessToken  = data.accessToken
            _refreshToken = data.refreshToken or _refreshToken
            savePrefs()
            if onSuccess then onSuccess() end
        else
            anonymousAuth(onSuccess, onError)
        end
    end, { headers = { ["Content-Type"] = "application/json" }, body = body, timeout = 15 })
end

-- ─── Offline Queue ──────────────────────────────────────────────────────────

local function enqueueOffline(method, path, body)
    if #_offlineQueue >= MAX_QUEUE_SIZE then
        table.remove(_offlineQueue, 1) -- Drop oldest
    end
    table.insert(_offlineQueue, {
        method    = method,
        path      = path,
        body      = body,
        timestamp = os.time(),
    })
    saveQueue()
end

local function flushQueue()
    if _isFlushing or #_offlineQueue == 0 then return end
    _isFlushing = true
    log("Flushing " .. #_offlineQueue .. " queued events")

    local function processNext()
        if #_offlineQueue == 0 then
            _isFlushing = false
            saveQueue()
            return
        end

        local item = _offlineQueue[1]
        httpRequest(item.method, item.path, item.body, function()
            table.remove(_offlineQueue, 1)
            timer.performWithDelay(100, processNext)
        end, function()
            _isFlushing = false
            saveQueue()
        end)
    end

    processNext()
end

local function enqueueOrSend(method, path, body)
    httpRequest(method, path, body, nil, function()
        enqueueOffline(method, path, body)
    end)
end

-- ─── Session Management ─────────────────────────────────────────────────────

local function startSession(onComplete)
    if _sessionActive then
        if onComplete then onComplete(_sessionId) end
        return
    end

    local body = json.encode({
        appKey      = _appKey,
        appVersion  = system.getInfo("appVersionString") or "1.0.0",
        osVersion   = getOSVersion(),
        deviceModel = getDeviceModel(),
        locale      = getLocale(),
        timezone    = getTimezone(),
    })

    httpRequest("POST", "/api/v1/sessions/start", body, function(data)
        if data and data.sessionId then
            _sessionId = data.sessionId
            _sessionActive = true
            log("Session started: " .. _sessionId)
            if onComplete then onComplete(_sessionId) end
        end
    end)
end

local function endSession(onComplete)
    if not _sessionActive or not _sessionId then return end

    local sid = _sessionId
    _sessionActive = false
    _sessionId = nil

    local body = json.encode({ sessionId = sid })
    httpRequest("POST", "/api/v1/sessions/end", body, function(data)
        if data then
            log("Session ended: " .. tostring(data.durationSeconds) .. "s")
            if onComplete then onComplete(data.durationSeconds) end
        end
    end)
end

-- ─── Application Lifecycle ──────────────────────────────────────────────────

local function onSystemEvent(event)
    if not _initialized then return end

    if event.type == "applicationSuspend" or event.type == "applicationExit" then
        endSession()
        saveQueue()
    elseif event.type == "applicationResume" then
        startSession()
        flushQueue()
    end
end

-- ─── Public API ─────────────────────────────────────────────────────────────

--- Initialize the SDK.
--- @param options table { apiBaseUrl, appKey, sdkVersion?, verbose? }
--- @param onComplete function(config) Called when init succeeds.
--- @param onError function(msg) Called on failure.
function M.init(options, onComplete, onError)
    if _initialized then
        if onComplete then onComplete(_config) end
        return
    end

    _apiBaseUrl  = (options.apiBaseUrl or "https://api.cubeage.com"):gsub("/$", "")
    _appKey      = options.appKey or ""
    _sdkVersion  = options.sdkVersion or "2.0.0"
    _verbose     = options.verbose or false

    -- Load persisted state
    local prefs = loadPrefs()
    _deviceId = resolveDeviceId()

    if prefs then
        _accessToken  = prefs.accessToken or ""
        _refreshToken = prefs.refreshToken or ""
        _userId       = prefs.userId or ""
    end

    loadQueue()

    local function onAuthDone()
        -- Fetch config
        httpRequest("GET", "/api/v1/apps/" .. _appKey .. "/config", nil, function(data)
            if data and data.config then
                _config = data.config
            end
            _initialized = true
            log("Init complete. userId=" .. _userId)

            -- Register lifecycle listener
            Runtime:addEventListener("system", onSystemEvent)

            -- Start session
            startSession()

            -- Track attribution on first launch
            if not prefs or prefs.firstLaunch ~= false then
                M.trackAttribution()
            end

            -- Flush offline queue
            flushQueue()

            savePrefs()
            if onComplete then onComplete(_config) end
        end, function(err)
            -- Config fetch failed, but still init
            _initialized = true
            Runtime:addEventListener("system", onSystemEvent)
            startSession()
            savePrefs()
            if onComplete then onComplete(_config) end
        end)
    end

    if #_accessToken > 0 then
        onAuthDone()
    else
        anonymousAuth(onAuthDone, function(err)
            if onError then onError(err) end
        end)
    end
end

--- Track a custom event.
function M.trackEvent(eventName, attributes)
    if not _initialized then logWarn("Not initialized"); return end
    local body = json.encode({
        appKey = _appKey,
        events = {{ name = eventName, properties = attributes or {} }},
    })
    enqueueOrSend("POST", "/api/v1/events/batch", body)
end

--- Track an IAP purchase.
function M.trackPurchase(productId, amount, currency, transactionId, receipt)
    if not _initialized then logWarn("Not initialized"); return end
    local body = json.encode({
        productId     = productId,
        amount        = amount,
        currency      = currency,
        transactionId = transactionId,
        receipt       = receipt,
    })
    enqueueOrSend("POST", "/api/v1/iap/validate", body)
end

--- Start a game session manually (auto-called on foreground).
function M.startSession(onComplete)
    if not _initialized then logWarn("Not initialized"); return end
    startSession(onComplete)
end

--- End the current game session manually (auto-called on background).
function M.endSession(onComplete)
    if not _initialized then return end
    endSession(onComplete)
end

--- Track install attribution.
function M.trackAttribution(onComplete)
    if not _initialized then logWarn("Not initialized"); return end
    local body = json.encode({
        appKey        = _appKey,
        platform      = getPlatform(),
        deviceId      = _deviceId,
        advertisingId = "", -- ATT/GAID not directly accessible in Solar2D
        appVersion    = system.getInfo("appVersionString") or "1.0.0",
        osVersion     = getOSVersion(),
        deviceModel   = getDeviceModel(),
        locale        = getLocale(),
    })
    httpRequest("POST", "/api/v1/attribution/install", body, function(data)
        if data then
            log("Attribution: " .. tostring(data.attributionId) .. " first=" .. tostring(data.isFirstInstall))
            if onComplete then onComplete(data.attributionId, data.isFirstInstall) end
        end
    end)
end

--- Track ad revenue from mediation SDK.
--- @param impressionData table { adNetwork, adUnit?, adFormat?, placement?, revenue, currency?, precision?, country? }
function M.trackAdRevenue(impressionData)
    if not _initialized then logWarn("Not initialized"); return end
    local body = json.encode({
        appKey    = _appKey,
        sessionId = _sessionId or "",
        impressions = {{
            adNetwork = impressionData.adNetwork or "unknown",
            adUnit    = impressionData.adUnit or "",
            adFormat  = impressionData.adFormat or "",
            placement = impressionData.placement or "",
            revenue   = impressionData.revenue or 0,
            currency  = impressionData.currency or "USD",
            precision = impressionData.precision or "estimated",
            country   = impressionData.country or "",
        }},
    })
    enqueueOrSend("POST", "/api/v1/revenue/ad", body)
end

--- Set a single user property.
function M.setUserProperty(key, value)
    M.setUserProperties({ [key] = value })
end

--- Set multiple user properties.
function M.setUserProperties(properties)
    if not _initialized then logWarn("Not initialized"); return end
    local body = json.encode({ properties = properties })
    enqueueOrSend("PUT", "/api/v1/user/properties", body)
end

--- Check for app updates.
function M.checkForUpdate(onComplete, onError)
    if not _initialized then
        if onError then onError("Not initialized") end
        return
    end
    httpRequest("GET", "/api/v1/apps/" .. _appKey .. "/update", nil, function(data)
        if onComplete then onComplete(data) end
    end, onError)
end

--- Get a remote config value.
function M.getConfig(key, default)
    if _config and _config[key] ~= nil then return _config[key] end
    return default
end

--- Get current user ID.
function M.getUserId()
    return _userId
end

--- Get current session ID.
function M.getSessionId()
    return _sessionId
end

--- Check if SDK is initialized.
function M.isInitialized()
    return _initialized
end

return M
