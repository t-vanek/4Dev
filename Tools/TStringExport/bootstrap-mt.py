"""Download the official OPUS-MT weights and convert them for local CPU inference."""
import json, pathlib, sys, urllib.request
from huggingface_hub import snapshot_download
from ctranslate2.converters import TransformersConverter

sys.stdout.reconfigure(encoding="utf-8")
root = pathlib.Path(sys.argv[1]).resolve()
repo = "Helsinki-NLP/opus-mt-tc-big-en-ces_slk"
metadata = json.load(urllib.request.urlopen("https://huggingface.co/api/models/" + repo))
revision = metadata["sha"]
print("Downloading", repo, revision, flush=True)
source = snapshot_download(repo, revision=revision, local_dir=root / "mt-source",
                           local_dir_use_symlinks=False,
                           allow_patterns=["*.json", "*.spm", "*.safetensors", "README.md"])
target = root / "opus-en-cs-int8"
if not (target / "model.bin").exists():
    print("Converting to local int8 CPU model", flush=True)
    TransformersConverter(source).convert(str(target), quantization="int8")
(root / "mt-model.json").write_text(json.dumps({"Model": repo, "Revision": revision,
    "License": "CC-BY-4.0", "Source": str(source), "Converted": str(target),
    "Attribution": "Jörg Tiedemann and Santhosh Thottingal, OPUS-MT, EAMT 2020; Helsinki-NLP"}, indent=2), encoding="utf-8")
print("Ready:", target, flush=True)
