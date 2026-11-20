# Vendored third-party source

All files under this directory keep their original `SoulsFormats` namespace and are
vendored as source, pinned to specific upstream commits, per the project hard rule
"Dependencies come from source in their official repositories, pinned to a commit.
No prebuilt third-party binaries."

| Upstream | Commit | License | Files |
|---|---|---|---|
| https://github.com/soulsmods/SoulsFormatsNEXT | `e25d1a5c3cda83b801fdefe00f4d2903892cc526` | GPLv3 (`LICENSE-SoulsFormatsNEXT.txt`) | `Utilities/**`, `Formats/DCX.cs`, `Formats/BHD5.cs`, `Formats/NVA.cs`, `Formats/Binder/**`, `Formats/MSB/**` |
| https://github.com/vawser/Smithbox (`src/Andre/SoulsFormats`) | `2145776b19446e78692a8caca87beaeb5788e4eb` | GPLv3 (`LICENSE-Smithbox.txt`) | `Formats/HKX/**` (Havok 2014 packfile / hkaiNavMesh decoder) |

Local modifications (kept minimal, marked where practical):

- Removed code paths that require prebuilt binary dependencies (Oodle native DLL,
  ZstdNet, BouncyCastle, DotNext, CommunityToolkit). DS3 only uses zlib ("DFLT") DCX
  compression, which is served by `System.IO.Compression`. Trimmed branches throw
  `NotSupportedException` instead.
- Minor API shims where the Smithbox HKX decoder expects helpers from its own fork
  of the utilities.

The RSA archive keys in `../../Crypto/ArchiveKeys.cs` are transcribed from
https://github.com/JKAnderson/UXM `ArchiveKeys.cs`, commit
`7da971b7e9a817b447335b5ebe9d845b1f98ad0d` (GPLv3). The raw-RSA block decryption and
the BHD5 path-hash dictionary approach follow UXM/BinderTool; the implementation here
is written against the .NET BCL (`System.Numerics.BigInteger`, `System.Security.Cryptography`).
