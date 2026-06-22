# Cubeage Game SDK

Cubeage Game SDK is the source package repository for Cubeage Platform SDK
integrations across Unity, Cocos Creator, and Solar2D. It provides client-side
auth, session, analytics, purchase, attribution, force-update, and offline queue
helpers for games that integrate with Cubeage platform APIs.

## Lifecycle

- State: `active`
- Layer: `integration`
- Machine manifest: [`.doctrine/project.json`](./.doctrine/project.json)

## Goals

- Own the Unity, Cocos Creator, and Solar2D SDK source packages in this repo.
- Keep the SDK API focused on Cubeage platform integration primitives for games.
- Preserve legacy-token migration behavior where the SDK explicitly implements it.

## Non-Goals

- Own backend platform API behavior, billing validation services, or analytics
  storage.
- Own game-specific feature logic, economy behavior, UI content, or product
  experiments.
- Own the newer Android/iOS native SDK v3 release workflow.

## Boundary

This repository owns source-distributed game-engine SDK integrations. It does
not own the Cubeage backend, host game code, production deployment, or the newer
native SDK release train. Consumers should depend on the package/source surfaces
listed below rather than reaching into sibling repositories.

## Public Surfaces

- Unity package: `Unity/Assets/CubeageSDK/package.json`
- Unity runtime API: `Unity/Assets/CubeageSDK/CubeageSDK.cs`
- Cocos Creator runtime API: `Cocos/CubeageSDK.ts`
- Cocos Creator configuration: `Cocos/CubeageSDKConfig.ts`
- Solar2D runtime module: `Solar2D/cubeage_sdk.lua`
- Solar2D configuration: `Solar2D/cubeage_sdk_config.lua`

## Delivery

The repository currently has no CI workflow and no registry-backed release
workflow. Source SDK changes land on `main`; package consumers need source
readback and game-integration smoke proof before production use. Versioned
package/release automation is an adoption gap rather than an established
control plane in this repository.
