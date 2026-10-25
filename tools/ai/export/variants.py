# Lighter variants of the exported graphs: int8 dynamic quantisation (CPU) and fp16 (GPU), inputs/outputs kept fp32.
# usage: python -I variants.py <in.onnx> <kind: int8|fp16> <out.onnx>
import sys, onnx
src, kind, out = sys.argv[1:4]
if kind == "int8":
    from onnxruntime.quantization import quantize_dynamic, QuantType
    ops = sys.argv[4].split(",") if len(sys.argv) > 4 else ["MatMul"]
    quantize_dynamic(src, out, weight_type=QuantType.QInt8, op_types_to_quantize=ops)
else:
    from onnxconverter_common import float16
    m = onnx.load(src)
    del m.graph.value_info[:]   # stale fp32 shape annotations make ORT reject converted Casts
    # the NSF sine source accumulates phase (CumSum/Mod/Sin): keep it in fp32 or the pitch drifts
    block = ["CumSum", "Mod", "Sin", "RandomNormalLike", "RandomUniformLike", "Range"]
    nodes = [n.name for n in m.graph.node if n.name.startswith("/dec/m_source/")]   # the whole sine source stays fp32
    m16 = float16.convert_float_to_float16(m, keep_io_types=True, op_block_list=block, node_block_list=nodes)
    onnx.save(m16, out)
print("ok", out)
