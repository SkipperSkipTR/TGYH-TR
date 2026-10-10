"""
Builds the release files of the Turkish patch.

    python tools/build_release.py

Output in dist/:
  TGYHTurkishPatch-v<version>.zip   the plugin package; extracts into the game folder as
                                    BepInEx/plugins/TGYHTurkishPatch/. Contains no BepInEx.
  TGYH_TR.exe                       the Windows installer

Upload both to a GitHub release (see DEVELOPMENT.md). The installers pick up the newest
release that has a TGYHTurkishPatch*.zip attached.

What goes into the package:
  - the plugin DLL, freshly built
  - texts/, fonts/ and videos/ from the installed plugin folder in the game
  - textures/ as prebuilt .tgyc files, encoded from the PNGs in the installed plugin folder

Requires: pip install UnityPy, and the .NET SDK.
"""
import os
import re
import shutil
import subprocess
import sys

import build_texture_pack

sys.stdout.reconfigure(encoding="utf-8")

REPO_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME_DIR = os.path.dirname(REPO_DIR)
PLUGIN_NAME = "TGYHTurkishPatch"
PLUGIN_SRC = os.path.join(REPO_DIR, "Plugin")
INSTALLER_SRC = os.path.join(REPO_DIR, "Installer")
INSTALLED = os.path.join(GAME_DIR, "BepInEx", "plugins", PLUGIN_NAME)
DIST = os.path.join(REPO_DIR, "dist")


def plugin_version():
    with open(os.path.join(PLUGIN_SRC, "Plugin.cs"), encoding="utf-8") as fh:
        return re.search(r'Version\s*=\s*"([^"]+)"', fh.read()).group(1)


def copy_files(src_dir, dst_dir, extensions):
    os.makedirs(dst_dir, exist_ok=True)
    count = 0
    for root, _, files in os.walk(src_dir):
        for name in files:
            if extensions and os.path.splitext(name)[1].lower() not in extensions:
                continue
            target = os.path.join(dst_dir, os.path.relpath(os.path.join(root, name), src_dir))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copy2(os.path.join(root, name), target)
            count += 1
    return count


def dotnet_build(project, *extra):
    subprocess.run(["dotnet", "build", project, "-c", "Release", "--nologo", "-v", "q", *extra], check=True)


def main():
    version = plugin_version()
    name = f"{PLUGIN_NAME}-v{version}"
    stage = os.path.join(DIST, name)
    plugin_out = os.path.join(stage, "BepInEx", "plugins", PLUGIN_NAME)

    print("== Building the plugin")
    dotnet_build(os.path.join(PLUGIN_SRC, PLUGIN_NAME + ".csproj"))

    if os.path.isdir(stage):
        shutil.rmtree(stage)
    os.makedirs(plugin_out)

    print("== Copying plugin files")
    shutil.copy2(os.path.join(INSTALLED, PLUGIN_NAME + ".dll"), plugin_out)
    with open(os.path.join(plugin_out, "version.txt"), "w", encoding="utf-8") as fh:
        fh.write(version + "\n")
    for folder, extensions in (("texts", {".csv", ".json"}), ("fonts", None), ("videos", {".mp4", ".webm", ".mov"})):
        n = copy_files(os.path.join(INSTALLED, folder), os.path.join(plugin_out, folder), extensions)
        print(f"   {folder}: {n} files")

    # Keep the translation under version control alongside the code.
    copy_files(os.path.join(INSTALLED, "texts"), os.path.join(REPO_DIR, "texts"), {".csv", ".json"})

    print("== Encoding textures")
    if build_texture_pack.build(os.path.join(plugin_out, "textures")) != 0:
        sys.exit("Texture pack failed; release not finished.")

    print("== Zipping")
    archive = shutil.make_archive(stage, "zip", stage)
    shutil.rmtree(stage)

    print("== Building the Windows installer")
    installer_out = os.path.join(INSTALLER_SRC, "bin", "Release")
    dotnet_build(os.path.join(INSTALLER_SRC, "TGYH_TR_Installer.csproj"), f"-p:Version={version}", "-o", installer_out)
    installer = shutil.copy2(os.path.join(installer_out, "TGYH_TR.exe"), DIST)

    print(f"Package:   {archive} ({os.path.getsize(archive) / 1048576:.0f} MB)")
    print(f"Installer: {installer} ({os.path.getsize(installer) / 1024:.0f} KB)")


if __name__ == "__main__":
    main()
