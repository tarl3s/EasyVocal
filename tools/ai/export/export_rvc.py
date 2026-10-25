# Exports RVC v2 (768-dim content, with pitch) generators as the two ONNX graphs EasyVocal.Ai runs:
#   <out>_enc.onnx : phone[1,T,768] f32, pitch[1,T] i64, lengths[1] i64 -> m_p[1,192,T], logs_p[1,192,T]
#   <out>_dec.onnx : z_p[1,192,N] f32, nsff0[1,M] f32, g[1,256,1] f32 -> audio[1,1,M*hop]   (M <= N: the flow sees
#                    N frames, the vocoder decodes only the last M)
# Weight norm is removed and constant folding is off, so every initializer keeps its state-dict name: EasyVocal.Ai
# rewrites a template's initializers with a voice's .pth tensors (OnnxRewriter), no PyTorch at run time.
# Dev-time only (run once per architecture; the outputs are committed / shipped, nothing of Python is).
#
# usage: python -I export_rvc.py <rvc_repo> templates <out_dir>           # weight-less v2 f0 32k/40k/48k templates (.onnx.gz)
#        python -I export_rvc.py <rvc_repo> full <model.pth> <sr> <out_prefix>   # one model with its weights
import gzip, os, sys, torch

rvc = sys.argv[1]
sys.path.insert(0, rvc)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rel_attn_patch; rel_attn_patch.apply()
from infer.module.models import SynthesizerTrnMs768NSFsid

ARCH = {
    "32k": dict(sr=32000, ups=[10, 8, 2, 2], ks=[20, 16, 4, 4], spec=513),
    "40k": dict(sr=40000, ups=[10, 10, 2, 2], ks=[16, 16, 4, 4], spec=1025),
    "48k": dict(sr=48000, ups=[12, 10, 2, 2], ks=[24, 20, 4, 4], spec=1025),
}


def make(cfg, nspk, hidden=192, filt=768, layers=6, up_ch=512, res_k=(3, 7, 11), res_d=((1, 3, 5),) * 3):
    m = SynthesizerTrnMs768NSFsid(cfg["spec"], 32, 192, hidden, filt, 2, layers, 3, 0, "1", list(res_k),
                                  [list(d) for d in res_d], cfg["ups"], up_ch, cfg["ks"], nspk, 256, cfg["sr"],
                                  is_half=False)
    del m.enc_q
    return m


class Enc(torch.nn.Module):
    def __init__(s, m): super().__init__(); s.enc_p = m.enc_p
    def forward(s, phone, pitch, lengths):
        m_p, logs_p, _ = s.enc_p(phone, pitch, lengths)
        return m_p, logs_p


class Dec(torch.nn.Module):
    def __init__(s, m): super().__init__(); s.flow = m.flow; s.dec = m.dec
    def forward(s, z_p, nsff0, g):
        mask = torch.ones_like(z_p[:, :1, :])
        z = s.flow(z_p, mask, g=g, reverse=True)
        z = z[:, :, z.shape[2] - nsff0.shape[1]:]
        return s.dec(z, nsff0, g=g)


def randomize(m):
    # every tensor distinct, or the exporter merges equal ones (all-ones LayerNorm gammas) into one initializer
    with torch.no_grad():
        for p in m.parameters(): p.normal_(0, 0.02)


def export(m, prefix, hidden_in=768):
    m.eval(); m.dec.remove_weight_norm(); m.flow.remove_weight_norm()
    T = 64
    phone = torch.randn(1, T, hidden_in); pitch = torch.randint(1, 255, (1, T)); lengths = torch.LongTensor([T])
    z_p = torch.randn(1, 192, T); f0 = torch.full((1, 40), 150.0); g = torch.randn(1, 256, 1)
    kw = dict(opset_version=17, do_constant_folding=False, dynamo=False)
    with torch.no_grad():
        torch.onnx.export(Enc(m), (phone, pitch, lengths), prefix + "_enc.onnx", input_names=["phone", "pitch", "lengths"],
                          output_names=["m_p", "logs_p"], dynamic_axes={"phone": {1: "T"}, "pitch": {1: "T"}, "m_p": {2: "T"}, "logs_p": {2: "T"}}, **kw)
        torch.onnx.export(Dec(m), (z_p, f0, g), prefix + "_dec.onnx", input_names=["z_p", "nsff0", "g"], output_names=["audio"],
                          dynamic_axes={"z_p": {2: "N"}, "nsff0": {1: "M"}, "audio": {2: "S"}}, **kw)


def strip(path, names):
    """Keeps name, type and dims of every initializer that is a model weight (the voice fills them); anything else
    the exporter made (constants) keeps its data. Writes <path>.gz and removes <path>."""
    import onnx
    model = onnx.load(path)
    for t in model.graph.initializer:
        if t.name in names:
            t.ClearField("raw_data"); t.ClearField("float_data")
    with gzip.open(path + ".gz", "wb", compresslevel=9) as f: f.write(model.SerializeToString())
    os.remove(path)


if sys.argv[2] == "templates":
    out = sys.argv[3]; os.makedirs(out, exist_ok=True)
    for srk, cfg in ARCH.items():
        torch.manual_seed(1)
        m = make(cfg, 109); randomize(m)
        prefix = os.path.join(out, f"rvc_v2_f0_{srk}")
        export(m, prefix)
        names = set(n for n, _ in m.named_parameters())
        for part in ("_enc.onnx", "_dec.onnx"): strip(prefix + part, names)
        print("template", srk, "ok")
else:
    pth, srk, prefix = sys.argv[3:6]
    ck = torch.load(pth, map_location="cpu", weights_only=False)
    sd = ck.get("weight") or ck.get("model") or ck
    sd = {k: v.float() for k, v in sd.items() if not k.startswith("enc_q")}
    m = make(ARCH[srk], sd["emb_g.weight"].shape[0])
    print(m.load_state_dict(sd, strict=False))
    export(m, prefix)
    print("ok")
