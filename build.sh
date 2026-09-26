#!/bin/sh
# Builds Apocasetter.dll against the game's own libraries (mono mcs).
M=${MANAGED:-/e/SteamLibrary/steamapps/common/Apocalypter/Apocalypter_Data/Managed}; B=${BEPCORE:-/e/SteamLibrary/steamapps/common/Apocalypter/BepInEx/core}
mcs -target:library -out:Apocasetter.dll -nostdlib -noconfig -optimize+ -langversion:7 \
  -r:$M/mscorlib.dll -r:$M/System.dll -r:$M/System.Core.dll -r:$M/netstandard.dll \
  -r:$B/BepInEx.dll -r:$B/0Harmony.dll \
  -r:$M/UnityEngine.dll -r:$M/UnityEngine.CoreModule.dll -r:$M/UnityEngine.IMGUIModule.dll \
  -r:$M/UnityEngine.InputLegacyModule.dll -r:$M/UnityEngine.ImageConversionModule.dll \
  -r:$M/UnityEngine.TextRenderingModule.dll -r:$M/UnityEngine.UI.dll -r:$M/UnityEngine.UIModule.dll \
  -r:$M/Unity.TextMeshPro.dll -r:$M/Unity.InputSystem.dll -r:$M/PlayMaker.dll -r:$M/Assembly-CSharp.dll \
  Plugin.cs SettingsUI.cs Theme.cs MiniJson.cs
