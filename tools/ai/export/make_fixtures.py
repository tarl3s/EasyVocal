# Makes the tiny test fixtures tests/EasyVocal.Ai.Tests/Fixtures/ uses (a few hundred KB, committed):
#   tiny.pth                  an RVC v2 40k "inference .pth" exactly as RVC saves one (zip, fp16, weight norm as
#                             weight_g/weight_v, config/sr/f0/version/info) of a tiny network (same layout as the
#                             real v2 40k one, small channel counts)
#   tiny_enc/dec.onnx.gz      the weight-less templates of that tiny network
#   tiny_ref_enc/dec.onnx     the same network exported by PyTorch with its (fp16-rounded) weights inside: the .pth
#                             import must sound the same as these
#   tiny_single.onnx          the same network as ONE graph in RVC WebUI's "Export Onnx" layout
#                             (phone, phone_lengths, pitch, pitchf, ds, rnd -> audio)
#   tiny.index + tiny_index_q.f32 + tiny_index_blend.f32
#                             a faiss IndexIVFFlat (what RVC's .index is) over random 768-d vectors, queries and
#                             RVC's retrieval blend of them at rate 0.75 (k = 8, 1/d^2 weights) for the reader test
#   tiny_hubert.onnx          a stand-in content encoder with HuBERT's inputs, outputs and frame count
#                             (source[1,S] -> feats[1,F,768], the same conv strides) for whole-pipeline tests
#   tiny_rmvpe.onnx           a stand-in pitch model with RMVPE's inputs and outputs (mel[1,128,N] -> [1,N,360])
# usage: python -I make_fixtures.py <rvc_repo> <out_dir>
import gzip, os, sys, numpy as np, torch

rvc, out = sys.argv[1:3]
os.makedirs(out, exist_ok=True)
sys.path.insert(0, rvc)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rel_attn_patch; rel_attn_patch.apply()
from infer.module.models import SynthesizerTrnMs768NSFsid
from torch.nn.utils import weight_norm

torch.manual_seed(7)
# spec, segment, inter, hidden, filter, heads, layers, kernel, dropout, resblock, res_k, res_d, ups, up_ch, ups_k, n_spk, gin, sr
config = [1025, 32, 192, 16, 32, 2, 1, 3, 0, "1", [3], [[1, 3, 5]], [10, 10, 2, 2], 32, [16, 16, 4, 4], 2, 256, 40000]
m = SynthesizerTrnMs768NSFsid(*config, is_half=False)
with torch.no_grad():
    for p in m.parameters(): p.normal_(0, 0.05)
m.eval()
# what RVC saves: fp16, enc_q dropped, weight norm still split (old weight_g / weight_v names)
sd = {k: v.half() for k, v in m.state_dict().items() if not k.startswith("enc_q")}
sd = {k.replace("parametrizations.weight.original0", "weight_g").replace("parametrizations.weight.original1", "weight_v"): v for k, v in sd.items()}
torch.save({"weight": sd, "config": config, "info": "tiny test model", "sr": "40k", "f0": 1, "version": "v2"}, os.path.join(out, "tiny.pth"))

# the reference network: the same fp16-rounded weights back in fp32, weight norm folded by PyTorch
ref = SynthesizerTrnMs768NSFsid(*config, is_half=False).eval()
back = {k: v.float() for k, v in m.state_dict().items()}
for k in list(back):
    if not k.startswith("enc_q"): back[k] = back[k].half().float()
ref.load_state_dict(back)
del ref.enc_q
ref.dec.remove_weight_norm(); ref.flow.remove_weight_norm()


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


class Single(torch.nn.Module):  # RVC WebUI's SynthesizerTrnMsNSFsidM.forward
    def __init__(s, m): super().__init__(); s.m = m
    def forward(s, phone, phone_lengths, pitch, pitchf, ds, rnd):
        g = s.m.emb_g(ds).unsqueeze(-1)
        m_p, logs_p, x_mask = s.m.enc_p(phone, pitch, phone_lengths)
        z_p = (m_p + torch.exp(logs_p) * rnd) * x_mask
        z = s.m.flow(z_p, x_mask, g=g, reverse=True)
        return s.m.dec(z * x_mask, pitchf, g=g)


T = 40
phone = torch.randn(1, T, 768); pitch = torch.randint(1, 255, (1, T)); lengths = torch.LongTensor([T])
z_p = torch.randn(1, 192, T); f0 = torch.full((1, 20), 150.0); g = torch.randn(1, 256, 1)
kw = dict(opset_version=17, dynamo=False)


def export_pair(model, prefix, folding):
    with torch.no_grad():
        torch.onnx.export(Enc(model), (phone, pitch, lengths), prefix + "_enc.onnx", input_names=["phone", "pitch", "lengths"],
                          output_names=["m_p", "logs_p"], dynamic_axes={"phone": {1: "T"}, "pitch": {1: "T"}, "m_p": {2: "T"}, "logs_p": {2: "T"}},
                          do_constant_folding=folding, **kw)
        torch.onnx.export(Dec(model), (z_p, f0, g), prefix + "_dec.onnx", input_names=["z_p", "nsff0", "g"], output_names=["audio"],
                          dynamic_axes={"z_p": {2: "N"}, "nsff0": {1: "M"}, "audio": {2: "S"}}, do_constant_folding=folding, **kw)


export_pair(ref, os.path.join(out, "tiny_ref"), True)

# the template: a differently-random copy (every tensor distinct), weights then stripped
tpl = SynthesizerTrnMs768NSFsid(*config, is_half=False).eval()
del tpl.enc_q
tpl.dec.remove_weight_norm(); tpl.flow.remove_weight_norm()
with torch.no_grad():
    for p in tpl.parameters(): p.normal_(0, 0.02)
export_pair(tpl, os.path.join(out, "tiny"), False)
import onnx
names = set(n for n, _ in tpl.named_parameters())
for part in ("tiny_enc.onnx", "tiny_dec.onnx"):
    p = os.path.join(out, part); mm = onnx.load(p)
    for t in mm.graph.initializer:
        if t.name in names: t.ClearField("raw_data"); t.ClearField("float_data")
    with gzip.open(p + ".gz", "wb", compresslevel=9) as f: f.write(mm.SerializeToString())
    os.remove(p)

single = SynthesizerTrnMs768NSFsid(*config, is_half=False).eval()
single.load_state_dict(back); del single.enc_q
single.dec.remove_weight_norm(); single.flow.remove_weight_norm()
with torch.no_grad():
    torch.onnx.export(Single(single), (phone, lengths, pitch, torch.full((1, T), 150.0), torch.LongTensor([0]), torch.randn(1, 192, T) * 0.66666),
                      os.path.join(out, "tiny_single.onnx"), input_names=["phone", "phone_lengths", "pitch", "pitchf", "ds", "rnd"],
                      output_names=["audio"], dynamic_axes={"phone": [1], "pitch": [1], "pitchf": [1], "rnd": [2]}, do_constant_folding=True, **kw)

try:
    import faiss
    rng = np.random.default_rng(3)
    big = rng.standard_normal((160, 768)).astype(np.float32)
    q = faiss.IndexFlatL2(768); ix = faiss.IndexIVFFlat(q, 768, 4)
    ix.train(big); ix.add(big); ix.nprobe = 1
    faiss.write_index(ix, os.path.join(out, "tiny.index"))
    queries = (big[:5] + 0.3 * rng.standard_normal((5, 768))).astype(np.float32)
    score, idx = ix.search(queries, k=8)
    w = np.square(1 / score); w /= w.sum(axis=1, keepdims=True)
    blend = np.sum(big[idx] * np.expand_dims(w, axis=2), axis=1)
    rate = 0.75
    res = (blend * rate + queries * (1 - rate)).astype(np.float32)
    queries.tofile(os.path.join(out, "tiny_index_q.f32")); res.tofile(os.path.join(out, "tiny_index_blend.f32"))
except ImportError:
    print("faiss not installed: no index fixture")


class TinyHubert(torch.nn.Module):  # HuBERT's conv strides (frame count) with tiny channels
    def __init__(s):
        super().__init__()
        ks, st = [10, 3, 3, 3, 3, 2, 2], [5, 2, 2, 2, 2, 2, 2]
        s.convs = torch.nn.ModuleList([torch.nn.Conv1d(1 if i == 0 else 8, 8, k, t, bias=False) for i, (k, t) in enumerate(zip(ks, st))])
        s.proj = torch.nn.Linear(8, 768)
    def forward(s, source):
        x = source.unsqueeze(1)
        for c in s.convs: x = torch.nn.functional.gelu(c(x))
        return s.proj(x.transpose(1, 2))


class TinyRmvpe(torch.nn.Module):  # RMVPE's interface: log-mel [1,128,N] -> salience [1,N,360]
    def __init__(s):
        super().__init__(); s.lin = torch.nn.Linear(128, 360)
    def forward(s, input):
        return torch.sigmoid(s.lin(input.transpose(1, 2)))


torch.manual_seed(11)
th, tr = TinyHubert().eval(), TinyRmvpe().eval()
with torch.no_grad():
    torch.onnx.export(th, (torch.randn(1, 16000),), os.path.join(out, "tiny_hubert.onnx"), input_names=["source"], output_names=["feats"],
                      dynamic_axes={"source": {1: "S"}, "feats": {1: "F"}}, opset_version=17, dynamo=False)
    torch.onnx.export(tr, (torch.randn(1, 128, 32),), os.path.join(out, "tiny_rmvpe.onnx"), input_names=["input"], output_names=["output"],
                      dynamic_axes={"input": {2: "N"}, "output": {1: "N"}}, opset_version=17, dynamo=False)
print("ok")
