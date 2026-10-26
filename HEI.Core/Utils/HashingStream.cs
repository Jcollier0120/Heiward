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

using System.Security.Cryptography;

namespace HEI.Core.Utils {
	/// <summary>
	/// Passes reads or writes through to another stream, or with none only takes the writes, and hashes (SHA-256) every
	/// byte on its way. Leaves the other stream open.
	/// </summary>
	sealed class HashingStream(Stream? inner) : Stream {
		readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		/// <summary>Bytes read before this stream was put in between, to hash first.</summary>
		public void Append(ReadOnlySpan<byte> bytes) => hash.AppendData(bytes);

		/// <summary>The hash of every byte so far.</summary>
		public byte[] Hash() => hash.GetCurrentHash();

		public override int Read(Span<byte> buffer) {
			int read = (inner ?? throw new NotSupportedException()).Read(buffer);
			hash.AppendData(buffer[..read]);
			return read;
		}
		public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

		public override void Write(ReadOnlySpan<byte> buffer) {
			hash.AppendData(buffer);
			inner?.Write(buffer);
		}
		public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

		public override bool CanRead => inner?.CanRead ?? false;
		public override bool CanWrite => inner?.CanWrite ?? true;
		public override bool CanSeek => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => inner?.Flush();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();

		protected override void Dispose(bool disposing) {
			if (disposing)
				hash.Dispose();
			base.Dispose(disposing);
		}
	}
}
