# AIM-9X IR Missile Overhaul — Nuclear Option BepInEx Mod

Adds AIM-9X-class capabilities to all IR-seeking missiles in Nuclear Option:

- **90° High Off-Boresight (HOBS)** — Launch IR missiles at targets up to 90° off your nose
- **Enhanced Turning** — Thrust-vector-control-level agility (increased torque and PID turn rate)
- **Lock-On After Launch (LOAL)** — Missiles actively search for IR targets if launched without lock or after losing lock

## Building

1. Copy the following DLLs from your game install into a `lib/` folder at the workspace root:
   - `NuclearOption_Data/Managed/Assembly-CSharp.dll`
   - `NuclearOption_Data/Managed/UnityEngine.CoreModule.dll`
   - `NuclearOption_Data/Managed/UnityEngine.PhysicsModule.dll`
   - `NuclearOption_Data/Managed/UniTask.dll`

2. Build:
   ```
   dotnet build AIM9XMod/AIM9XMod.csproj -c Release
   ```

3. Copy `AIM9XMod/bin/Release/net472/AIM9XMod.dll` to `BepInEx/plugins/`

## Configuration

After first run, edit `BepInEx/config/com.modder.aim9xmod.cfg`:

| Setting | Default | Description |
|---------|---------|-------------|
| EnableHighOffBoresight | true | 90° off-boresight launch |
| EnableEnhancedTurning | true | AIM-9X turning performance |
| EnableLOAL | true | Lock-on after launch |
| OffBoresightAngle | 90 | Max launch angle (degrees) |
| MaxTurnRate | 12 | PID turn rate limit (vanilla ~3) |
| TorqueMultiplier | 3 | Torque multiplier for IR missiles |
| LOALSearchAngle | 90 | Seeker search cone for LOAL |
| LOALSearchTime | 8 | Seconds to search before going ballistic |
