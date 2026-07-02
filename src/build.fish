#!/usr/bin/env fish
# build.fish - Compile only, no deployment
# Run 'fish deploy.fish' after to deploy
# Windows users don't need fish: dotnet build src/SailwindCoop/SailwindCoop.csproj -c Release -p:GameDir="C:/path/to/Sailwind"

set LOCAL_GAME (set -q SAILWIND_GAME_DIR; and echo $SAILWIND_GAME_DIR; or echo "$HOME/.local/share/Steam/steamapps/common/Sailwind")
set PROJECT_DIR (dirname (status -f))
set MAIN_REPO (realpath "$PROJECT_DIR/..")

# Mono.CSharp.dll location (needed for GameInspector's runtime C# eval)
# Override with MONO_CSHARP_DIR env var if your path differs
if not set -q MONO_CSHARP_DIR
    # Common locations (Proton GE Wine Mono)
    for candidate in \
        /usr/share/steam/compatibilitytools.d/proton-ge-custom/files/share/wine/mono/*/lib/mono/4.5 \
        "$HOME/.steam/root/compatibilitytools.d/proton-ge-custom/files/share/wine/mono/*/lib/mono/4.5" \
        /usr/lib/mono/4.5
        if test -f "$candidate/Mono.CSharp.dll"
            set MONO_CSHARP_DIR "$candidate"
            break
        end
    end
    if not set -q MONO_CSHARP_DIR
        echo "⚠️  Mono.CSharp.dll not found. Set MONO_CSHARP_DIR env var. GameInspector build may fail."
    end
end

# Ensure libs/ exists (for worktrees - libs is gitignored)
if not test -d "$PROJECT_DIR/SailwindCoop/libs"
    if test -d "$MAIN_REPO/src/SailwindCoop/libs"
        echo "📦 Symlinking libs/ from main repo (worktree detected)"
        ln -s "$MAIN_REPO/src/SailwindCoop/libs" "$PROJECT_DIR/SailwindCoop/libs"
    else
        echo "❌ libs/ folder not found. Extract Facepunch.Steamworks NuGet package to src/SailwindCoop/libs/"
        exit 1
    end
end

# Restore if needed (offline mode - nuget.org may be unreachable)
if not test -f "$PROJECT_DIR/SailwindCoop/obj/project.assets.json"
    echo "📦 Restoring NuGet packages (offline mode)..."
    dotnet restore "$PROJECT_DIR/SailwindCoop/SailwindCoop.csproj" \
        -p:GameDir="$LOCAL_GAME" \
        --source ~/.nuget/packages \
        --no-cache
    or exit 1
end

# Build SailwindCoop
echo "🔨 Building SailwindCoop..."
dotnet build "$PROJECT_DIR/SailwindCoop/SailwindCoop.csproj" -c Release --no-restore -p:GameDir="$LOCAL_GAME"
or exit 1

# Restore GameInspector if needed (offline mode)
if not test -f "$PROJECT_DIR/GameInspector/obj/project.assets.json"
    echo "📦 Restoring GameInspector NuGet packages (offline mode)..."
    dotnet restore "$PROJECT_DIR/GameInspector/GameInspector.csproj" \
        -p:GameDir="$LOCAL_GAME" \
        --source ~/.nuget/packages \
        --no-cache
    or exit 1
end

# Build GameInspector
echo "🔨 Building GameInspector..."
dotnet build "$PROJECT_DIR/GameInspector/GameInspector.csproj" -c Release --no-restore -p:GameDir="$LOCAL_GAME" -p:MonoCSharpDir="$MONO_CSHARP_DIR"
or exit 1

echo "✅ Build complete:"
echo "   - $PROJECT_DIR/SailwindCoop/bin/Release/net472/SailwindCoop.dll"
echo "   - $PROJECT_DIR/GameInspector/bin/Release/GameInspector.dll"
echo "   Run 'fish deploy.fish' to deploy"
