# Contributing to VortexDownloader ⚡

Thank you for your interest in contributing to **VortexDownloader**!

## 🛡️ Critical: Clean-Room Engineering Policy
VortexDownloader is strictly an independent **Clean-Room implementation** based on public standards (RFC 9110, RFC 7233).
To protect the project and all contributors:
- **DO NOT** commit or reference proprietary binaries, disassemblies, or reverse-engineered pseudocode from Tonec Inc. or third parties.
- **DO NOT** commit precompiled executables (`.exe`, `.dll`, `.pdb`, `.bin`, `.sys`).
- All code must be original, clean C# / .NET 9.

## 🛠️ Development Setup
1. Clone the repo:
   ```bash
   git clone https://github.com/Armone0/VortexDownloader.git
   cd VortexDownloader
   ```
2. Build solution:
   ```bash
   dotnet build
   ```
3. Run test suite:
   ```bash
   dotnet test
   ```

## 📝 Pull Request Guidelines
1. Ensure all 18 existing unit tests pass before submitting.
2. If adding a new feature or algorithm fix, write corresponding tests in `tests/VortexDownloader.Tests/`.
3. Follow idiomatic modern C# 13 patterns (nullable references, file-scoped namespaces, pattern matching).
4. Keep commit messages concise following Conventional Commits (`feat:`, `fix:`, `docs:`, `chore:`).
