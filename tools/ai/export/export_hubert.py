# Exports RVC's content encoder (HuBERT base as RVC ships it in Transformers format, lj1995/VoiceConversionWebUI
# hubert_base/, MIT) to ONNX:  source[1,S] f32 (16 kHz, not normalised) -> feats[1,F,768] (layer 12 = RVC v2's features)
# Dev-time only.
# usage: python -I export_hubert.py <hubert_base_dir> full <out.onnx>          # with its weights (then variants.py int8)
#        python -I export_hubert.py <hubert_base_dir> template <out.onnx.gz>   # weight-less: EasyVocal.Ai fills it from pytorch_model.bin
import gzip, os, sys, torch
from transformers import HubertModel

src, mode, out = sys.argv[1:4]
m = HubertModel.from_pretrained(src, attn_implementation="eager", dtype=torch.float32).float().eval()
conv = m.encoder.pos_conv_embed.conv
try:  # weight norm -> one plain weight named like the state dict's (EasyVocal folds weight_g/weight_v itself)
    torch.nn.utils.parametrize.remove_parametrizations(conv, "weight", leave_parametrized=True)
except Exception:
    torch.nn.utils.remove_weight_norm(conv)
if mode == "template":
    torch.manual_seed(1)
    with torch.no_grad():
        for p in m.parameters(): p.normal_(0, 0.02)


class W(torch.nn.Module):
    def __init__(s, m): super().__init__(); s.m = m
    def forward(s, source): return s.m(input_values=source).last_hidden_state


x = torch.randn(1, 16000)
path = out[:-3] if out.endswith(".gz") else out
with torch.no_grad():
    torch.onnx.export(W(m), (x,), path, input_names=["source"], output_names=["feats"],
                      dynamic_axes={"source": {1: "S"}, "feats": {1: "F"}}, opset_version=17,
                      do_constant_folding=(mode != "template"), dynamo=False)
if mode == "template":
    import onnx
    names = set("m." + n for n, _ in m.named_parameters())
    model = onnx.load(path)
    stripped = 0
    for t in model.graph.initializer:
        if t.name in names:
            t.ClearField("raw_data"); t.ClearField("float_data"); stripped += 1
    with gzip.open(out, "wb", compresslevel=9) as f: f.write(model.SerializeToString())
    os.remove(path)
    print("stripped", stripped, "of", len(model.graph.initializer))
print("ok")
