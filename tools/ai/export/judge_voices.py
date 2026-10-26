# Picks and checks the bundled AI voices without listening (dev time only; see AGENTS.md "Bundled voices"):
#   WavLM-base-plus-sv x-vectors (speaker similarity, cosine) and an MFCC timbre print (19 MFCCs' mean and spread over
#   the louder frames, the same print as tests/EasyVocal.Ai.Tests/Helpers/VoiceMeasure.cs) of each voice rendered on
#   several recordings (tools/EasyVocal.AiPack: blends -> <id>__<recording>.wav).
# usage:
#   python -I judge_voices.py survey <sids dir> <out.npz>                 x-vectors of the base model's 109 speakers
#   python -I judge_voices.py candidates <survey.npz> <survey.csv> <emb_g.npy> <recipe.json> <out.json>
#                                                                         3-way blends per voice (main speaker kept)
#   python -I judge_voices.py judge <renders dir> [--pick <candidates.json> <out recipe.json>]
#                                                                         similarity table; with --pick: the set of one
#                                                                         candidate per voice that is least alike
import sys, os, glob, json, itertools, numpy as np, librosa, torch
from transformers import AutoFeatureExtractor, WavLMForXVector

_fx = _m = None
def xvec(y):
    global _fx, _m
    if _m is None:
        _fx = AutoFeatureExtractor.from_pretrained("microsoft/wavlm-base-plus-sv")
        _m = WavLMForXVector.from_pretrained("microsoft/wavlm-base-plus-sv").eval()
    with torch.no_grad():
        e = _m(**_fx(y, sampling_rate=16000, return_tensors="pt")).embeddings[0]
    return torch.nn.functional.normalize(e, dim=-1).numpy()

def timbre(y):
    S = np.abs(librosa.stft(y, n_fft=512, hop_length=160, win_length=400, window="hamming", center=False)) ** 2
    mel = librosa.feature.melspectrogram(S=S, sr=16000, n_mels=40, fmin=60, fmax=7600, htk=True, norm=None)
    c = librosa.feature.mfcc(S=np.log(mel + 1e-10), n_mfcc=20, dct_type=2, norm=None)[1:] / 2  # VoiceMeasure's DCT scale
    e = 10 * np.log10(np.sum(S, 0) + 1e-12)
    keep = e > e.max() - 30
    c = c[:, keep] if keep.any() else c
    return np.concatenate([c.mean(1), c.std(1)])

def load(p): return librosa.load(p, sr=16000)[0]

def survey(sids, out):
    ids, vecs = [], []
    for p in sorted(glob.glob(os.path.join(sids, "sid*_key0.wav"))):
        sid = int(os.path.basename(p)[3:6])
        v = (xvec(load(p)) + xvec(load(p.replace("key0", "key+12")))) / 2
        ids.append(sid); vecs.append(v / np.linalg.norm(v))
    np.savez(out, ids=np.array(ids), vecs=np.array(vecs))
    print(f"{len(ids)} speakers -> {out}")

def candidates(npz, csv, embp, recipe, out, per=6, alternates=2, weights=(0.6, 0.25, 0.15)):
    d = np.load(npz); ids = [int(s) for s in d["ids"]]; V = d["vecs"]; sim = V @ V.T
    lean, cen = {}, {}
    for line in open(csv).read().splitlines()[1:]:
        f = line.split(","); lean[int(f[0])] = float(f[3]) + float(f[4]); cen[int(f[0])] = float(f[1])
    r = json.load(open(recipe, encoding="utf-8"))
    mains = [int(v["mix"][0][0]) for v in r["voices"]]
    ix = {s: i for i, s in enumerate(ids)}
    cands = []
    for k, v in enumerate(r["voices"]):
        m0 = mains[k]; female = lean[m0] > 0
        others = [o for o in mains if o != m0]
        same = [s for s in ids if s not in mains and (lean[s] > 0) == female]
        # other speakers that could carry this voice: as female/male-leaning and as bright, further from the other voices
        alt = [s for s in same if abs(lean[s] - lean[m0]) < 0.3 and abs(cen[s] / cen[m0] - 1) < 0.15]
        alt = sorted(alt, key=lambda s: max(sim[ix[s], ix[o]] for o in others))[:alternates]
        n = 0
        for m in [m0] + alt:
            pool = [s for s in same if s != m]
            # a partner near this voice's speaker and far from the other voices' speakers
            score = {s: sim[ix[s], ix[m]] - max(sim[ix[s], ix[o]] for o in others) for s in pool}
            top = sorted(pool, key=lambda s: -score[s])[:4]
            for p, q in list(itertools.combinations(top, 2))[:per]:
                c = dict(v); c["id"] = f"{v['id']}--{n}"; c["slot"] = v["id"]; n += 1
                c["mix"] = [[m, weights[0]], [p, weights[1]], [q, weights[2]]]
                cands.append(c)
    r2 = dict(r); r2["voices"] = cands
    json.dump(r2, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(f"{len(cands)} candidates -> {out}")

def read_renders(folder):
    # the features are kept next to the renders (features.npz): a re-run doesn't load WavLM again for the same files
    cache = os.path.join(folder, "features.npz")
    old = dict(np.load(cache, allow_pickle=True)["f"].item()) if os.path.exists(cache) else {}
    X, T, f = {}, {}, {}
    for p in sorted(glob.glob(os.path.join(folder, "*__*.wav"))):
        vid, rec = os.path.basename(p)[:-4].split("__")
        key = os.path.basename(p) + ":" + str(os.path.getmtime(p))
        if key not in old:
            y = load(p); old[key] = (xvec(y), timbre(y))
        f[key] = old[key]
        X[(vid, rec)], T[(vid, rec)] = old[key]
    np.savez(cache, f=np.array(f, dtype=object))
    return X, T


def table(X, T, voices, recs, label):
    n = len(voices)
    S = np.zeros((n, n)); D = np.zeros((n, n))
    for i, j in itertools.product(range(n), range(n)):
        S[i, j] = np.mean([X[(voices[i], r)] @ X[(voices[j], r)] for r in recs])
        D[i, j] = np.mean([np.linalg.norm(T[(voices[i], r)] - T[(voices[j], r)]) for r in recs])
    own = [np.mean([X[(v, a)] @ X[(v, b)] for a, b in itertools.combinations(recs, 2)]) for v in voices]
    # recognisable: the voice on one recording is nearest to itself on another (WavLM and MFCC)
    ok_x = ok_t = total = 0
    for v in voices:
        for a, b in itertools.permutations(recs, 2):
            total += 1
            ok_x += max(voices, key=lambda w: X[(v, a)] @ X[(w, b)]) == v
            ok_t += min(voices, key=lambda w: np.linalg.norm(T[(v, a)] - T[(w, b)])) == v
    off = S[~np.eye(n, dtype=bool)]; offd = D[~np.eye(n, dtype=bool)]
    print(f"== {label}: WavLM similarity between voices (same recording) max {off.max():.3f} mean {off.mean():.3f}; "
          f"MFCC distance min {offd.min():.1f} mean {offd.mean():.1f}; own similarity across recordings mean {np.mean(own):.3f} min {np.min(own):.3f}; "
          f"recognised {ok_x}/{total} (WavLM) {ok_t}/{total} (MFCC)")
    w = max(len(v) for v in voices)
    print(" " * w + " own  | " + " ".join(f"{k:5d}" for k in range(n)))
    for i, v in enumerate(voices):
        print(f"{v:{w}} {own[i]:.3f} | " + " ".join("  -  " if i == j else f"{S[i, j]:.3f}" for j in range(n)))
    return off.max(), off.mean(), offd.min(), offd.mean()

def judge(folder, pick=None, out=None):
    X, T = read_renders(folder)
    recs = sorted({r for _, r in X})
    voices = sorted({v for v, _ in X})
    if not pick:
        table(X, T, voices, recs, folder)
        return
    r = json.load(open(pick, encoding="utf-8"))
    slots = []
    for c in r["voices"]:
        if c["slot"] not in slots: slots.append(c["slot"])
    by = {s: [c["id"] for c in r["voices"] if c["slot"] == s and (c["id"], recs[0]) in X] for s in slots}
    def sim(a, b): return np.mean([X[(a, q)] @ X[(b, q)] for q in recs])
    def own(a): return np.mean([X[(a, p)] @ X[(a, q)] for p, q in itertools.combinations(recs, 2)])
    def mfcc(a, b): return np.mean([np.linalg.norm(T[(a, q)] - T[(b, q)]) for q in recs])
    ids = [c for s in slots for c in by[s]]
    P = {(a, b): sim(a, b) for a, b in itertools.combinations(ids, 2)}
    M = {(a, b): mfcc(a, b) for a, b in itertools.combinations(ids, 2)}
    def pair(d, a, b): return d[(a, b)] if (a, b) in d else d[(b, a)]
    # less alike = a lower mean and a lower top similarity (WavLM) and farther timbre prints (MFCC, ~25 per 0.25)
    def cost(sel):
        v = list(sel.values()); pairs = list(itertools.combinations(v, 2))
        s = [pair(P, a, b) for a, b in pairs]; m = [pair(M, a, b) for a, b in pairs]
        return np.mean(s) + 0.5 * max(s) - 0.02 * np.mean(m) - 0.01 * min(m)
    rng = np.random.default_rng(7)
    best_sel, best_cost = None, 1e9
    for start in range(40):  # coordinate descent from many starts
        cur = {s: (max(by[s], key=own) if start == 0 else by[s][rng.integers(len(by[s]))]) for s in slots}
        for _ in range(10):
            changed = False
            for s in slots:
                c = min(by[s], key=lambda c: cost({**cur, s: c}))
                if c != cur[s]: cur[s] = c; changed = True
            if not changed: break
        if cost(cur) < best_cost: best_sel, best_cost = dict(cur), cost(cur)
    cur = best_sel
    table(X, T, [cur[s] for s in slots], recs, "picked")
    chosen = {c["id"]: c for c in r["voices"]}
    base = dict(r); base["voices"] = []
    for s in slots:
        c = dict(chosen[cur[s]]); c["id"] = s; c.pop("slot", None); base["voices"].append(c)
    json.dump(base, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("picked:", {s: chosen[cur[s]]["mix"] for s in slots})

if __name__ == "__main__":
    a = sys.argv[1:]
    if a[0] == "survey": survey(a[1], a[2])
    elif a[0] == "candidates": candidates(*a[1:6])
    elif a[0] == "judge": judge(a[1], *(a[3:5] if len(a) > 2 and a[2] == "--pick" else []))
