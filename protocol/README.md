# Link protocol

The Lethal Company plugin and the Minecraft (Fabric) mod talk over one Windows shared-memory mapping
(`Local\LethalCraft_v1`). The layout is mirrored by hand in two places, and they must always agree:

- `plugin/src/Link/Proto.cs` (C#, host side: creates the mapping)
- `fabric/src/main/java/dev/lethalcraft/link/Proto.java` (Java, Minecraft side: opens it)

Bump `Version` in both whenever the layout changes. The header magic is `"LTHC"`.

RepoCraft's `repocraft_protocol.h` was dropped: it was SkyCraft's original layout and had drifted from the
real one (different magic, no host-state block, no multiplayer header). Phase 0 follow-up: a test that
parses both files' constants and fails if they differ.
