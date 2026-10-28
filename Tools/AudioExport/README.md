# Original-client audio export

```powershell
dotnet run --project Tools/AudioExport -c Release -- 'C:/Program Files (x86)/4Story' .runtime/audio/NEW-export
```

Reads `Index/TClientW.IDX` and exports the original audio from `Data/Media`. These `.TMD` files
contain ordinary WAV or MP3 data. The media index's final DWORD contains media type/volume,
not a byte offset. File signatures determine the output extension, including audio that was
registered as DirectShow media. No audio is decoded/re-encoded and no source/client file is edited.

The output folder must be new and outside the installed source client. The exporter checks
index bounds, path containment, WAV chunk bounds and byte-identical copies using SHA-256. It also
detects source changes during extraction. The manifest retains game IDs, original paths/types/volume,
WAV parameters and hashes. Missing source references are reported rather than invented or downloaded.
Music and sound playlists use relative paths and can be moved with the exported folder.

## Client compatibility check

Miniaudio is the playback library, not a file format. This client's `TMiniAudio.h` enables
WAV and MP3 decoding. Its `.TMD` assets may retain their names: miniaudio recognizes their data.
Use `Build-MiniaudioCheck.ps1` to build an x86 checker against that exact header/configuration.
Pass a TSV list containing `mediaType<TAB>absolutePath` per line and a new report path.
It fully decodes every file, checks finite samples/frame counts and seeking, then exercises
the client's decode/copy or streaming loader flags in an offline engine. No device is opened.
Type 0 is checked only by the decoder because the client routes it through DirectShow.
No encoding, resampling or volume changes are necessary for already supported assets.
