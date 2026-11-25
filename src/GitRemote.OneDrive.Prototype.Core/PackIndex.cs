using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GitRemote.OneDrive.Prototype;

internal sealed class PackIndex
{
    private const int FanoutStart = 8;
    private const int ObjectIdsStart = FanoutStart + 256 * sizeof(uint);

    private readonly byte[] _bytes;
    private readonly int _hashSize;

    internal PackIndex(byte[] bytes, string pack, int hashLength)
    {
        _bytes = bytes;
        _hashSize = hashLength / 2;
        if (bytes.Length < ObjectIdsStart + 2 * _hashSize || ReadNumber(0) != 0xff744f63 || ReadNumber(4) != 2)
            throw InvalidIndex(pack);

        uint previous = 0;
        for (var i = 0; i < 256; i++)
        {
            var end = ReadFanout(i);
            if (end < previous)
                throw InvalidIndex(pack);
            previous = end;
        }

        var minimumLength = ObjectIdsStart + (long)previous * (_hashSize + 8) + 2 * _hashSize;
        if (minimumLength > bytes.Length || (bytes.Length - minimumLength) % 8 != 0)
            throw InvalidIndex(pack);

        var expectedPackHash = Convert.FromHexString(pack[5..]);
        var packChecksum = bytes.AsSpan(bytes.Length - 2 * _hashSize, _hashSize);
        var content = bytes.AsSpan(0, bytes.Length - _hashSize);
        var digest = _hashSize == 20 ? SHA1.HashData(content) : SHA256.HashData(content);
        if (!packChecksum.SequenceEqual(expectedPackHash) || !bytes.AsSpan(bytes.Length - _hashSize).SequenceEqual(digest))
            throw InvalidIndex(pack);
    }

    internal bool Contains(ReadOnlySpan<byte> oid)
    {
        var first = oid[0];
        var left = first == 0 ? 0 : (int)ReadFanout(first - 1);
        var right = (int)ReadFanout(first) - 1;
        while (left <= right)
        {
            var middle = left + (right - left) / 2;
            var candidate = _bytes.AsSpan(ObjectIdsStart + middle * _hashSize, _hashSize);
            var order = candidate.SequenceCompareTo(oid);
            if (order == 0)
                return true;
            if (order < 0)
                left = middle + 1;
            else
                right = middle - 1;
        }
        return false;
    }

    private uint ReadFanout(int first) => ReadNumber(FanoutStart + first * sizeof(uint));
    private uint ReadNumber(int start) => BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(start, sizeof(uint)));
    private static IOException InvalidIndex(string pack) => new($"Invalid Git index '{pack}.idx'.");
}
