# Post-fix for onnxconverter-common fp16 graphs: model Casts "to float" that now feed fp16 ops become "to float16"
# (the converter leaves them, and ORT then rejects the graph). Casts the converter itself inserted
# (graph input/output and around blocked ops) are left alone. usage: python -I variants_fix.py <fp16.onnx>
import sys, onnx
p = sys.argv[1]; m = onnx.load(p)
blocked = {"CumSum", "Mod", "Sin", "RandomNormalLike", "RandomUniformLike", "Range"}
consumers = {}
for n in m.graph.node:
    for i in n.input: consumers.setdefault(i, []).append(n)
outs = {o.name for o in m.graph.output}
fixed = 0
for n in m.graph.node:
    if n.op_type != "Cast" or "_cast" in n.name.lower() or n.output[0] in outs: continue
    to = next(a for a in n.attribute if a.name == "to")
    if to.i != 1: continue
    cs = consumers.get(n.output[0], [])
    if any(c.op_type in blocked or c.name.startswith("/dec/m_source/") for c in cs) or n.name.startswith("/dec/m_source/"): continue
    to.i = 10; fixed += 1
del m.graph.value_info[:]
onnx.save(m, p); print("fixed", fixed, p)
