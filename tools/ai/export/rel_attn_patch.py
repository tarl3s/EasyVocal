# Trace-friendly relative attention for RVC's TextEncoder (window_size=10). Mathematically identical to
# infer/module/attentions.py's pad/reshape version (zero-padded relative embeddings contribute 0 outside the window)
# but every length stays a tensor, so one ONNX graph works for any number of frames.
import math, torch
import torch.nn.functional as F

def attention(self, query, key, value, mask=None):
    b, d, _ = key.size()
    h, k = self.n_heads, self.k_channels
    q = query.view(b, h, k, -1).transpose(2, 3) / math.sqrt(k)
    kk = key.view(b, h, k, -1).transpose(2, 3)
    v = value.view(b, h, k, -1).transpose(2, 3)
    scores = torch.matmul(q, kk.transpose(-2, -1))                       # [b,h,l,l]
    w = self.window_size
    ar = torch.cumsum(torch.ones_like(q[0, 0, :, 0], dtype=torch.long), 0) - 1   # 0..l-1, traced
    off = ar.unsqueeze(0) - ar.unsqueeze(1) + w                            # [l,l]: j - i + w
    valid = ((off >= 0) & (off <= 2 * w)).to(scores.dtype)
    rel = torch.matmul(q, self.emb_rel_k.unsqueeze(0).transpose(-2, -1))   # [b,h,l,2w+1]
    idx = off.clamp(0, 2 * w).unsqueeze(0).unsqueeze(0).expand(b, h, -1, -1)
    scores = scores + torch.gather(rel, 3, idx) * valid
    if mask is not None:
        scores = scores.masked_fill(mask == 0, -1e4)
    p = F.softmax(scores, dim=-1)
    out = torch.matmul(p, v)
    kr = torch.arange(2 * w + 1, device=q.device) - w                     # relative offsets
    jidx = ar.unsqueeze(1) + kr.unsqueeze(0)                              # [l,2w+1]: i + off
    jvalid = ((jidx >= 0) & (jidx <= ar[-1])).to(p.dtype)
    jc = torch.minimum(jidx.clamp(min=0), ar[-1].expand_as(jidx))
    relw = torch.gather(p, 3, jc.unsqueeze(0).unsqueeze(0).expand(b, h, -1, -1)) * jvalid
    out = out + torch.matmul(relw, self.emb_rel_v.unsqueeze(0))
    return out.transpose(2, 3).contiguous().view(b, d, -1), p

def apply():
    from infer.module import attentions
    attentions.MultiHeadAttention.attention = attention
