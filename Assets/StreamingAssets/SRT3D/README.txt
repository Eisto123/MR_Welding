Place your SRT3D model files here. Each trackable object needs three files:

  ObjectName.obj               - mesh file
  ObjectName.srt3d             - precomputed SRT3D region data (RENAME from .meta, see below)
  ObjectName_init_pose.txt     - initial 4x4 pose matrix (row-major, one value per line)

IMPORTANT — SRT3D .meta renaming rule:
  The pysrt3d tool generates a file named ObjectName.meta.
  Unity reserves the .meta extension for its own asset metadata.
  If you place a .meta file here, Unity will try to parse it, fail, and delete it.

  Before copying into this folder, rename the SRT3D file:
    MyTrackObj.meta  →  MyTrackObj.srt3d

  Then set the Inspector field "Meta File Name" on Srt3dAndroidBridge to:
    MyTrackObj.srt3d

  The native plugin does not care about the extension — it reads the file contents directly.

These files are copied at runtime from StreamingAssets to Application.persistentDataPath/SRT3D/
before the native tracker is initialized.

Example layout:
  MyTrackObj.obj
  MyTrackObj.srt3d             ← renamed from MyTrackObj.meta
  MyTrackObj_init_pose.txt
