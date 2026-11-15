#!/bin/sh
# Compile a clip script against Rhino 9's RhinoCommon before sending it to Rhino. Rhino reports NOTHING when a
# script fails to compile (no output, no log, no error.txt), so a compile error looks like a hang.
#   check-script.sh <clip-script.cs>
# Only RhinoCommon + System.Drawing.Common are referenced, like the Rhino script environment, so code that needs more
# (e.g. `CaptureToBitmap(...) ?? throw ...`, CS0012 System.Private.Windows.*) fails here too.
set -e
SCRIPT="$1"
[ -f "$SCRIPT" ] || { echo "usage: check-script.sh <clip-script.cs>"; exit 2; }
WORK="${SIZZLE_WORK:-$(cygpath -m "$LOCALAPPDATA")/freppuccino-sizzle}"
C="$WORK/_compilecheck"
mkdir -p "$C"
cat > "$C/cc.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings></PropertyGroup>
  <ItemGroup>
    <Reference Include="RhinoCommon"><HintPath>C:\Program Files\Rhino 9\System\RhinoCommon.dll</HintPath></Reference>
    <Reference Include="System.Drawing.Common"><HintPath>C:\Program Files\Rhino 9\System\netcore\System.Drawing.Common.dll</HintPath></Reference>
  </ItemGroup>
</Project>
EOF
cp "$SCRIPT" "$C/Program.cs"
if (cd "$C" && dotnet build -nologo -v q 2>&1 | grep -E ' error ' | sed 's/^.*Program\.cs/  line/' | sort -u) | grep -q .; then
  (cd "$C" && dotnet build -nologo -v q 2>&1 | grep -E ' error ' | sed 's/ \[.*$//; s/^.*Program\.cs/  /' | sort -u)
  echo "compile errors: fix before running"; exit 1
fi
echo "compiles OK"
