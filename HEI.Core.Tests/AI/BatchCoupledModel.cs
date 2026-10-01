// /*
//     Copyright (C) 2026 Jeremy Collier
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System.Text;
using HEI.Core.AI;

namespace HEI.Core.Tests.AI;

/// <summary>
/// A tiny embedder that quantizes its input the way the int8 DINOv2 does its activations:
/// DynamicQuantizeLinear, one scale for the whole tensor, the whole batch. So a frame's
/// embedding depends on the frames run with it, as the real model's did (pixel_values
/// [B,3,224,224] → quantize → dequantize → mean per channel → fixed projection → [B,384]).
/// Written as ONNX protobuf by hand: the test project has no ONNX writer.
/// </summary>
static class BatchCoupledModel {

	public static string Write(string path) {
		var rng = new Random(5);
		var weights = new float[3 * EmbeddingMath.Dimensions];
		for (int i = 0; i < weights.Length; i++)
			weights[i] = (float)(rng.NextDouble() * 2 - 1);

		byte[] graph = Concat(
			Node("DynamicQuantizeLinear", new[] { "pixel_values" }, new[] { "q", "scale", "zero" }),
			Node("DequantizeLinear", new[] { "q", "scale", "zero" }, new[] { "dq" }),
			Node("ReduceMean", new[] { "dq" }, new[] { "mean" }, Ints("axes", 2, 3), Int("keepdims", 0)),
			Node("MatMul", new[] { "mean", "w" }, new[] { "embedding" }),
			Message(2, Str("batch-coupled")),
			Message(5, Concat(Varint(1, 3), Varint(1, EmbeddingMath.Dimensions), Varint(2, 1), Floats(4, weights), Message(8, Str("w")))),
			Message(11, ValueInfo("pixel_values", "batch", 3, OnnxEmbedder.InputSide, OnnxEmbedder.InputSide)),
			Message(12, ValueInfo("embedding", "batch", EmbeddingMath.Dimensions)));
		byte[] model = Concat(
			Varint(1, 7), // IR version 7: opset 13
			Message(8, Varint(2, 13)),
			Message(7, graph));
		File.WriteAllBytes(path, model);
		return path;
	}

	static byte[] Node(string op, string[] inputs, string[] outputs, params byte[][] attributes) =>
		Message(1, Concat(inputs.Select(i => Message(1, Str(i)))
			.Concat(outputs.Select(o => Message(2, Str(o))))
			.Append(Message(4, Str(op)))
			.Concat(attributes.Select(a => Message(5, a)))
			.ToArray()));

	static byte[] Ints(string name, params long[] values) =>
		Concat(new[] { Message(1, Str(name)) }.Concat(values.Select(v => Varint(8, v))).Append(Varint(20, 7)).ToArray());

	static byte[] Int(string name, long value) => Concat(Message(1, Str(name)), Varint(3, value), Varint(20, 2));

	/// <summary>A float tensor's name and shape; a string is a symbolic dimension.</summary>
	static byte[] ValueInfo(string name, params object[] dims) {
		byte[] shape = Concat(dims.Select(d => Message(1, d is string s ? Message(2, Str(s)) : Varint(1, Convert.ToInt64(d)))).ToArray());
		byte[] tensorType = Concat(Varint(1, 1), Message(2, shape)); // elem_type FLOAT
		return Concat(Message(1, Str(name)), Message(2, Message(1, tensorType)));
	}

	static byte[] Str(string s) => Encoding.UTF8.GetBytes(s);

	static byte[] Floats(int field, float[] values) {
		var bytes = new byte[values.Length * 4];
		Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
		return Message(field, bytes); // packed
	}

	static byte[] Varint(int field, long value) => Concat(Key(field, 0), VarintBytes((ulong)value));

	/// <summary>A length-delimited field: a string, bytes, a packed array or a sub-message.</summary>
	static byte[] Message(int field, byte[] payload) => Concat(Key(field, 2), VarintBytes((ulong)payload.Length), payload);

	static byte[] Key(int field, int wireType) => VarintBytes((ulong)((field << 3) | wireType));

	static byte[] VarintBytes(ulong value) {
		var bytes = new List<byte>();
		do {
			byte b = (byte)(value & 0x7F);
			value >>= 7;
			bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
		} while (value != 0);
		return bytes.ToArray();
	}

	static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
