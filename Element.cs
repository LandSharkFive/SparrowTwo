using System.Buffers.Binary;
using System.Text;

namespace SparrowTwo
{

    /// <summary>
    /// Represents a fundamental key-value pair stored within the B+ Tree.
    /// This structure is designed for fixed-width disk serialization, ensuring 
    /// predictable offsets during binary I/O operations.
    /// </summary>
    public struct Element : IComparable<Element> 
    {
        public int Key;   // The key of the element
        public string Data; // That data that each element contains

        /// <summary> Initializes a new empty element with sentinel values. </summary>
        public Element() { Key = -1; Data = String.Empty; }

        /// <summary> Initializes an element with a specific key and data pair. </summary>
        public Element(int key, string data) { Key = key; Data = data; }

        /// <summary> Returns a sentinel element representing an empty or null state. </summary>
        public static Element GetDefault() => new Element(-1, String.Empty);

        /// <summary> Compares elements based solely on their Key property. </summary>
        public int CompareTo(Element other)
        {
            return this.Key.CompareTo(other.Key);
        }

        /// <summary> Returns a formatted string for debugging: [Key, Data]. </summary>
        public override string ToString()
        {
            return $"[{Key}, {Data}] ";
        }

        /// <summary>
        /// Returns the serialized byte length of this element (4-byte integer key plus UTF-8 encoded string payload).
        /// </summary>
        public int GetSerializedSize()
        {
            string value = this.Data ?? string.Empty;
            return sizeof(int) + Encoding.UTF8.GetByteCount(value);
        }

        /// <summary>
        /// Serializes this element into a little-endian byte array containing the 4-byte key followed by UTF-8 string bytes.
        /// </summary>
        public byte[] ToBytes()
        {
            string value = this.Data ?? string.Empty;
            int payloadByteCount = Encoding.UTF8.GetByteCount(value);
            byte[] bytes = new byte[sizeof(int) + payloadByteCount];

            Span<byte> span = bytes.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, sizeof(int)), this.Key);
            Encoding.UTF8.GetBytes(value, span.Slice(sizeof(int)));

            return bytes;
        }

    }
}
