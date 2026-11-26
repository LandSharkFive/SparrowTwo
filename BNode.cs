using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace SparrowTwo
{
    /// <summary>
    /// BNode: A Slotted-Page B+ Tree Node implementation.
    /// 
    /// PHYSICAL LAYOUT:
    /// [Header] [Child Page Ids (Internal Only)] [Slot Table] [Data Heap]
    /// 
    /// The Slot Table and the Data Heap both grow forward from the top.
    /// </summary>

    public class BNode
    {
        // ##########################################
        // # 1. CONSTANTS & LAYOUT
        // ##########################################

        private readonly byte[] Buffer;
        public byte[] GetBuffer() => Buffer;

        private const int OFF_LEAF = 0;  // 4 byte
        private const int OFF_PAGE_ID = 4;   // 4 bytes. 
        private const int OFF_NUM_KEYS = 8;  // 4 bytes
        private const int OFF_NEXT_LEAF = 12; // 4 bytes
        private const int HEADER_SIZE = 16;

        private const int KID_SIZE = 4;    // int PageId
        private const int RECORD_SIZE = 8; // int Key + int Data.
        private const int SLOT_SIZE = 8;   // 4 bytes offset + 4 bytes length

        public int Order { get; set; }
        public int PageSize { get; set; }

        // ##########################################
        // # 2. CONSTRUCTORS & INITIALIZATION
        // ##########################################

        /// <summary> Creates a fresh, empty node initialized with a header. </summary>
        public BNode(int order, int pageSize, bool isLeaf, int id)
        {
            Order = order;
            PageSize = pageSize;
            Buffer = new byte[pageSize];
            Id = id;
            Leaf = isLeaf;
            NumKeys = 0;
            NextLeafId = -1;

            // FIX: Initialize internal node child slots to -1 instead of letting them default to 0
            if (!isLeaf)
            {
                // An internal node can have up to Order + 1 child pointers
                int maxPossibleChildren = order + 1;
                for (int i = 0; i < maxPossibleChildren; i++)
                {
                    SetChildId(i, -1);
                }
            }
        }

        /// <summary> Wraps an existing byte array (loaded from disk) into a BNode object. </summary>
        public BNode(int order, int pageSize, byte[] existingBuffer)
        {
            Order = order;
            PageSize = pageSize;
            Buffer = existingBuffer;

            // 1. READ THE LEAF.
            this.Leaf = BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(OFF_LEAF, 4)) == 1;

            // 1. READ THE ID 
            this.Id = BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(OFF_PAGE_ID, 4));

            // 2. READ THE COUNTS 
            this.NumKeys = BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(OFF_NUM_KEYS, 4));

            // 3. READ THE NEXT LEAF ID 
            this.NextLeafId = BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(OFF_NEXT_LEAF, 4));
        }

        /// <summary> Calculates capacity based on PageSize, accounting for header, slot table, and kid table overhead. </summary>
        public static int CalculateMaxOrder(int pageSize)
        {
            // Fixed overhead: Header (16B) + The baseline extra child pointer (+1 kid = 4B)
            int fixedOverhead = HEADER_SIZE + KID_SIZE;
            int usableSpace = pageSize - fixedOverhead;

            // Slotted-page cost per entry: Record (8B) + Slot (8B) + Child Pointer (4B)
            int perEntryCost = RECORD_SIZE + SLOT_SIZE + KID_SIZE;

            // We subtract 1 as a safety buffer for splitting/merging transients
            return (usableSpace / perEntryCost) - 1;
        }


        // ##########################################
        // # 3. PROPERTIES (Header Access)
        // ##########################################

        public int Id
        {
            get => BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(OFF_PAGE_ID, 4));
            set => BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(OFF_PAGE_ID, 4), value);
        }

        public int NumKeys
        {
            get => BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(OFF_NUM_KEYS, 4));
            set => BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(OFF_NUM_KEYS, 4), value);
        }

        public bool Leaf
        {
            get => BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(OFF_LEAF, 4)) == 1;
            set => BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(OFF_LEAF, 4), value ? 1 : 0 );

        }

        public int NextLeafId
        {
            get => BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(OFF_NEXT_LEAF, 4));
            set => BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(OFF_NEXT_LEAF, 4), value);
        }


        // ##########################################
        // # 4. ADDRESS ENGINE
        // ##########################################

        /// <summary> Child pointers follow the header in Internal nodes. </summary>
        private int KidTableStart => HEADER_SIZE;

        private int SlotTableStart => KidTableStart + ((Order + 1) * KID_SIZE);

        /// <summary> The physical byte where data records (Keys/Values) are allowed to begin. </summary>
        private int DataAreaStart => SlotTableStart + (Order * SLOT_SIZE);

        // ##########################################
        // # 6. DATA & CHILD ACCESS (The Cargo)
        // ##########################################

        /// <summary>
        /// Calculates the serialized byte size of a key-value record (4-byte integer key plus UTF-8 string payload).
        /// </summary>
        private static int GetRecordSize(string data)
        {
            int payloadBytes = Encoding.UTF8.GetByteCount(data ?? string.Empty);
            return sizeof(int) + payloadBytes; // 4-byte int key + UTF-8 string bytes
        }

        /// <summary>
        /// Calculates the absolute byte offset of a record within the physical page buffer.
        /// </summary>
        public int GetDataOffset(int index)
        {
            if (index < 0 || index >= NumKeys) throw new IndexOutOfRangeException();
            int slotAddress = SlotTableStart + (index * SLOT_SIZE);
            return BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(slotAddress, 4));
        }

        /// <summary>
        /// Retrieves the length of the data record at the specified index by reading the slot directory entry. 
        /// </summary>
        public int GetDataLength(int index)
        {
            if (index < 0 || index >= NumKeys) throw new IndexOutOfRangeException();
            int slotAddress = SlotTableStart + (index * SLOT_SIZE);
            return BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(slotAddress + 4, 4));
        }

        /// <summary>
        /// Calculates the next available byte offset in the data area where a new record can be safely appended.
        /// </summary>
        
        public int GetNextFreeDataOffset()
        {
            // If there are no keys, data starts exactly at the beginning of the Data Area
            if (NumKeys == 0) return DataAreaStart;

            int maxDataOffset = DataAreaStart;

            for (int i = 0; i < NumKeys; i++)
            {
                int slotAddress = SlotTableStart + (i * SLOT_SIZE);
                int recordOffset = BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(slotAddress, 4));
                int recordLength = BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(slotAddress + 4, 4));

                if (recordOffset + recordLength > maxDataOffset)
                {
                    maxDataOffset = recordOffset + recordLength;
                }
            }

            return maxDataOffset;
        }

        /// <summary>
        /// Compacts the internal buffer by removing fragmentation and rewriting active elements contiguously.
        /// </summary>
        /// <remarks>
        /// This method performs defragmentation (garbage collection) on the underlying byte buffer. 
        /// It backs up all active elements, resets the write pointer to the beginning of the data area, 
        /// and rewrites the records sequentially. Finally, it updates the slot table entries with the 
        /// new, optimized offsets and zeroes out any stale data left over at the end of the buffer.
        /// </remarks>
        public void Compact()
        {
            if (NumKeys == 0) return;

            // Create a temporary backup of the current elements
            List<Element> elements = GetAllElements();

            // Reset data pointer back to the start of the data area
            int currentWriteOffset = DataAreaStart;

            for (int i = 0; i < NumKeys; i++)
            {
                string value = elements[i].Data ?? string.Empty;
                int strByteCount = Encoding.UTF8.GetByteCount(value);
                int recordBytes = sizeof(int) + strByteCount;

                // Write the key and UTF-8 string contiguously
                Span<byte> recordSpan = Buffer.AsSpan(currentWriteOffset, recordBytes);
                BinaryPrimitives.WriteInt32LittleEndian(recordSpan.Slice(0, sizeof(int)), elements[i].Key);
                Encoding.UTF8.GetBytes(value, recordSpan.Slice(sizeof(int)));

                // Update the slot table with the new offset and exact record byte length
                int targetSlotAddress = SlotTableStart + (i * SLOT_SIZE);
                BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(targetSlotAddress, 4), currentWriteOffset);
                BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(targetSlotAddress + 4, 4), recordBytes);

                currentWriteOffset += recordBytes;
            }

            // Zero out the remaining stale buffer tail
            int remainingSpace = Buffer.Length - currentWriteOffset;
            if (remainingSpace > 0)
            {
                Buffer.AsSpan(currentWriteOffset, remainingSpace).Clear();
            }
        }

        /// <summary>
        /// Retrieves a 32-bit integer key from the specified logical index.
        /// Uses high-performance BinaryPrimitives to 'peek' into the raw byte buffer.
        /// </summary>
        public int GetKey(int index)
        {
            if (index < 0 || index >= NumKeys) throw new IndexOutOfRangeException();
            int offset = GetDataOffset(index);
            return BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(offset, 4));
        }


        /// <summary>
        /// Retrieves the UTF-8 string payload associated with the key at the given index.
        /// In the slotted layout, the string data immediately follows the 4-byte key.
        /// </summary>
        public string GetData(int index)
        {
            if (index < 0 || index >= NumKeys) throw new IndexOutOfRangeException();

            int offset = GetDataOffset(index);
            int recordLength = GetDataLength(index);
            int stringByteCount = recordLength - sizeof(int);

            if (stringByteCount <= 0) return string.Empty;

            return Encoding.UTF8.GetString(Buffer.AsSpan(offset + sizeof(int), stringByteCount));
        }


        /// <summary> Retrieves the 4-byte PageId from the Kid Table for internal nodes. </summary>
        public int GetChildId(int index)
        {
            if (index < 0 || index > NumKeys)
                throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} is invalid for Node {Id}.");
            if (Leaf) return -1;

            int address = KidTableStart + (index * KID_SIZE);
            return BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(address, 4));
        }

        /// <summary> Writes a 4-byte PageId into the Kid Table at the specified index, mapping the branch to a child node. </summary>
        public void SetChildId(int index, int childPageId)
        {
            int address = KidTableStart + (index * KID_SIZE);
            BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(address, 4), childPageId);
        }

        /// <summary> 
        /// Physically shifts the tail of the child pointer table to the right by one slot.
        /// Used during an internal node insertion to open a 'hole' for a new child ID.
        /// </summary>
        public void ShiftChildrenRight(int index)
        {
            int childrenToShift = (NumKeys + 1) - index;
            if (childrenToShift <= 0) return;

            int sourceOffset = KidTableStart + (index * KID_SIZE);
            int destOffset = KidTableStart + ((index + 1) * KID_SIZE);

            Debug.Assert(destOffset >= HEADER_SIZE && destOffset + (childrenToShift * KID_SIZE) <= DataAreaStart);

            System.Buffer.BlockCopy(Buffer, sourceOffset, Buffer, destOffset, childrenToShift * KID_SIZE);
        }

        /// <summary> 
        /// Shifts a range of child pointers one slot to the LEFT to close a gap.
        /// Used during internal node deletions or rebalancing. 
        /// </summary>
        public void ShiftChildrenLeft(int index)
        {
            // Cannot shift left into a negative index
            if (index <= 0 || index > NumKeys) return;

            int childrenToShift = (NumKeys + 1) - index;
            if (childrenToShift <= 0) return;

            int sourceOffset = KidTableStart + (index * KID_SIZE);
            int destOffset = KidTableStart + ((index - 1) * KID_SIZE);

            System.Buffer.BlockCopy(Buffer, sourceOffset, Buffer, destOffset, childrenToShift * KID_SIZE);

            // Clear the now-stale last child slot
            SetChildId(NumKeys, -1);
        }

        // ##########################################
        // # 7. SEARCH & NAVIGATION
        // ##########################################

        /// <summary> 
        /// /// Performs a binary search through the keys of a leaf node to locate a specific key 
        /// or determine its potential insertion point.
        /// </summary>
        public int FindIndex(int targetKey)
        {
            int low = 0;
            int high = NumKeys - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                int midKey = GetKey(mid);
                if (midKey == targetKey) return mid;
                if (midKey < targetKey) low = mid + 1;
                else high = mid - 1;
            }
            return ~low;
        }

        /// <summary>
        /// Performs a binary search on an internal node to find the correct child branch index 
        /// for a given key, properly handling internal routing boundaries. 
        /// </summary>
        public int FindChildBranchIndex(int key)
        {
            int low = 0, high = NumKeys - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                if (key >= GetKey(mid)) low = mid + 1;
                else high = mid - 1;
            }
            return low;
        }


        /// <summary> Converts the result of a binary search into a positive index where a new key should be physically placed to maintain sort order. </summary>
        public int FindInsertionPoint(int targetKey)
        {
            int index = FindIndex(targetKey);
            return index >= 0 ? index : ~index;
        }

        // ##########################################
        // # 8. MUTATION (The Heavy Lifters)
        // ##########################################

        /// <summary>
        /// Inserts a key-data record at logical index <paramref name="pos"/> while keeping
        /// the slot table in sorted key order, shifting internal child pointers right when
        /// needed, and compacting fragmented page space if the data heap cannot fit the record.
        /// </summary>
        public void InsertAt(int pos, int key, string data, int childPageId = -1)
        {
            if (NumKeys >= Order)
                throw new InvalidOperationException($"Node {Id} is at maximum capacity ({Order} keys).");

            string value = data ?? string.Empty;
            int strByteCount = Encoding.UTF8.GetByteCount(value);
            int recordBytes = sizeof(int) + strByteCount;

            // Calculate the next free offset based on current fragmentation
            int newDataOffset = GetNextFreeDataOffset();
            int totalSlotTableSpaceNeeded = SlotTableStart + ((NumKeys + 1) * SLOT_SIZE);

            // If physically out of space at the tail, compact to reclaim fragmented space
            if (newDataOffset + recordBytes > Buffer.Length || newDataOffset < totalSlotTableSpaceNeeded)
            {
                this.Compact();

                // Recalculate offsets after compaction
                newDataOffset = GetNextFreeDataOffset();

                if (newDataOffset + recordBytes > Buffer.Length || newDataOffset < totalSlotTableSpaceNeeded)
                {
                    throw new InvalidOperationException($"Page {Id} is genuinely out of physical byte space. Max Order/PageSize configuration mismatch.");
                }
            }

            int slotsToMove = NumKeys - pos;

            // 1. Shift the Slot Table entries to the right
            if (slotsToMove > 0)
            {
                int srcSlotOffset = SlotTableStart + (pos * SLOT_SIZE);
                int destSlotOffset = SlotTableStart + ((pos + 1) * SLOT_SIZE);
                System.Buffer.BlockCopy(Buffer, srcSlotOffset, Buffer, destSlotOffset, slotsToMove * SLOT_SIZE);
            }

            // 2. Handle Child Pointers for internal nodes
            if (!this.Leaf)
            {
                this.ShiftChildrenRight(pos);
                this.SetChildId(pos + 1, childPageId);
            }

            // 3. Append the physical data record safely (4-byte key + UTF-8 string)
            Span<byte> recordSpan = Buffer.AsSpan(newDataOffset, recordBytes);
            BinaryPrimitives.WriteInt32LittleEndian(recordSpan.Slice(0, sizeof(int)), key);
            Encoding.UTF8.GetBytes(value, recordSpan.Slice(sizeof(int)));

            // 4. Write the offset and actual record byte length into the slot
            int targetSlotAddress = SlotTableStart + (pos * SLOT_SIZE);
            BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(targetSlotAddress, 4), newDataOffset);
            BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(targetSlotAddress + 4, 4), recordBytes);

            // 5. Update Header
            this.NumKeys++;
        }


        /// <summary> Deletes a record slot and its associated child branch by shifting slot table entries. </summary>
        public void RemoveKeyAndChildAt(int pos)
        {
            if (pos < 0 || pos >= NumKeys) throw new IndexOutOfRangeException();

            int originalNumKeys = this.NumKeys;
            int moveCount = originalNumKeys - 1 - pos;

            // 1. Shift Slots Left
            if (moveCount > 0)
            {
                int slotSrc = SlotTableStart + ((pos + 1) * SLOT_SIZE);
                int slotDest = SlotTableStart + (pos * SLOT_SIZE);
                Buffer.AsSpan(slotSrc, moveCount * SLOT_SIZE).CopyTo(Buffer.AsSpan(slotDest));
            }

            // 2. Shift Children Left (Standard behavior: drop the right-hand child pointer at pos + 1)
            if (!Leaf)
            {
                int childrenToShift = originalNumKeys - 1 - pos; // Safe count fixed
                if (childrenToShift > 0)
                {
                    int kidSrc = KidTableStart + ((pos + 2) * KID_SIZE);
                    int kidDest = KidTableStart + ((pos + 1) * KID_SIZE);
                    Buffer.AsSpan(kidSrc, childrenToShift * KID_SIZE).CopyTo(Buffer.AsSpan(kidDest));
                }
            }

            // 3. Update Header
            this.NumKeys--;

            // Clear out the trailing dead slot directory entry explicitly
            int deadSlotAddress = SlotTableStart + (this.NumKeys * SLOT_SIZE);
            Buffer.AsSpan(deadSlotAddress, SLOT_SIZE).Clear();

            // 4. Hard Hygiene: Zero out inactive pointer slots.
            if (!Leaf)
            {
                int activeChildCount = this.NumKeys + 1;
                int maxPossibleChildren = Order + 1;
                for (int i = activeChildCount; i < maxPossibleChildren; i++)
                {
                    SetChildId(i, -1);
                }
            }

            this.Compact();
        }

        /// <summary>
        /// Specialized helper for a right sibling internal node that has given away Kid[0].
        /// Drops Key[0] and shifts all child pointers left starting from Kid[1] to close the gap.
        /// </summary>
        public void RemoveFirstKeyAndLeftChildInternal()
        {
            if (Leaf) throw new InvalidOperationException("Only valid for internal nodes.");
            if (NumKeys == 0) return;

            int originalNumKeys = this.NumKeys;

            // 1. Shift Slot Directory entries Left (Drop Key[0] / Slot[0])
            int moveCount = originalNumKeys - 1;
            if (moveCount > 0)
            {
                int slotSrc = SlotTableStart + (1 * SLOT_SIZE);
                int slotDest = SlotTableStart + (0 * SLOT_SIZE);
                Buffer.AsSpan(slotSrc, moveCount * SLOT_SIZE).CopyTo(Buffer.AsSpan(slotDest));
            }

            // 2. Shift Children Left: Kid[1..originalNumKeys] move down to Kid[0..originalNumKeys-1]
            int childrenToShift = originalNumKeys;
            int kidSrc = KidTableStart + (1 * KID_SIZE);
            int kidDest = KidTableStart + (0 * KID_SIZE);
            Buffer.AsSpan(kidSrc, childrenToShift * KID_SIZE).CopyTo(Buffer.AsSpan(kidDest));

            // 3. Decrement key count FIRST
            this.NumKeys--;

            // 4. Clear the dead trailing slot directory entry (0 is valid for empty slot offset/length)
            int deadSlotAddress = SlotTableStart + (this.NumKeys * SLOT_SIZE);
            Buffer.AsSpan(deadSlotAddress, SLOT_SIZE).Clear();

            // 5. Explicitly set trailing dead child pointer to -1 (DO NOT use Clear() here!)
            SetChildId(this.NumKeys + 1, -1);

            // 6. Defragment the slotted data heap safely
            this.Compact();
        }


        /// <summary>
        /// Splits the current node into two, partitioning the keys and pointers.
        /// LEAF SPLIT: The separator key is COPIED to the parent; the original remains in the leaf sibling.
        /// INTERNAL SPLIT: The separator key is PROMOTED to the parent and removed from the children 
        /// to maintain the (N keys, N+1 children) branching balance.
        /// </summary>
        public (BNode Sibling, int SeparatorKey) Split(int newId)
        {
            BNode sibling = new BNode(this.Order, this.PageSize, this.Leaf, newId);
            int midIndex = this.NumKeys / 2;
            int separatorKey = this.GetKey(midIndex);

            if (Leaf)
            {
                // LEAF SPLIT: Copy separator to parent, but keep it in the leaf sibling
                int keysToMove = this.NumKeys - midIndex;
                TransferRangeTo(sibling, midIndex, keysToMove);

                this.NumKeys = midIndex;
                sibling.NumKeys = keysToMove;

                sibling.NextLeafId = this.NextLeafId;
                this.NextLeafId = sibling.Id;
            }
            else
            {
                // INTERNAL SPLIT: Promote separatorKey, it is NOT kept in children.
                int moveStartOffset = midIndex + 1;
                int keysToMove = this.NumKeys - moveStartOffset;

                // 1. Transfer records cleanly to the new sibling one-by-one
                for (int i = 0; i < keysToMove; i++)
                {
                    int sourceIdx = moveStartOffset + i;
                    int key = this.GetKey(sourceIdx);
                    string data = this.GetData(sourceIdx);

                    // Use InsertAt but explicitly pass -1 for the transient child pointer
                    // so it doesn't try to borrow uninitialized junk during construction.
                    sibling.InsertAt(i, key, data, -1);
                }

                // 2. NOW copy the real child pointers to the sibling cleanly in one bulk transfer
                this.CopyChildrenTo(sibling, moveStartOffset, 0, keysToMove + 1);

                // Sibling's child count must match its logical elements
                sibling.NumKeys = keysToMove;

                // 3. HYGIENE: Clear the promoted key and the moved pointers from this node
                for (int i = midIndex; i < this.NumKeys; i++)
                {
                    int offset = GetDataOffset(i);
                    int length = GetDataLength(i);
                    if (length > 0)
                    {
                        Buffer.AsSpan(offset, length).Clear();
                    }

                    int slotAddress = SlotTableStart + (i * SLOT_SIZE);
                    Buffer.AsSpan(slotAddress, SLOT_SIZE).Clear();
                }

                // Clear child pointers out with -1 using our updated clean engine
                this.ClearChildPointers(midIndex + 1, (this.Order + 1) - (midIndex + 1));

                this.NumKeys = midIndex;
                this.Compact();
            }

            return (sibling, separatorKey);
        }

        /// <summary>
        /// Determines whether the node is full and cannot accept any more keys.
        /// </summary>
        public bool IsFull(string data)
        {
            // 1. Logical capacity check
            if (this.NumKeys >= Order) return true;

            // 2. Physical byte capacity check (header + kids + slot table + data heap)
            int nextSlotCount = this.NumKeys + 1;
            int slotTableEnd = SlotTableStart + (nextSlotCount * SLOT_SIZE);

            int incomingRecordBytes = GetRecordSize(data);
            int compactedHeapBytes = 0;

            for (int i = 0; i < this.NumKeys; i++)
            {
                compactedHeapBytes += GetDataLength(i);
            }

            int estimatedDataEnd = DataAreaStart + compactedHeapBytes + incomingRecordBytes;

            // Also guard against the slot table colliding with the start of the data area
            return slotTableEnd > DataAreaStart || estimatedDataEnd > Buffer.Length;
        }

        /// <summary>
        /// Performs a bulk physical transfer of child pointers from this node to a sibling.
        /// Used during an internal node split to move the right-hand pointers to the new node.
        /// </summary>
        public void CopyChildrenTo(BNode destination, int sourceIndex, int destIndex, int count)
        {
            if (count <= 0) return;

            int sourceOffset = KidTableStart + (sourceIndex * KID_SIZE);
            int destOffset = KidTableStart + (destIndex * KID_SIZE);

            // Optimized bulk move between two different node buffers
            System.Buffer.BlockCopy(this.Buffer, sourceOffset, destination.Buffer, destOffset, count * KID_SIZE);
        }

        /// <summary> 
        /// Zeroes out a range of child pointers in the physical buffer.
        /// Essential for 'Hygiene' to ensure stale Page IDs do not persist after a shift or split.
        /// </summary>
        /// 
        public void ClearChildPointers(int startIndex, int count)
        {
            if (count <= 0) return;

            for (int i = 0; i < count; i++)
            {
                SetChildId(startIndex + i, -1);
            }
        }


        /// <summary> Migrates a sequential range of slots and their associated child pointers to a destination node. </summary>
        public void TransferRangeTo(BNode destination, int startIdx, int count)
        {
            if (count <= 0) return;

            // 1. Copy records cleanly into the destination
            for (int i = 0; i < count; i++)
            {
                int sourceIdx = startIdx + i;
                int key = this.GetKey(sourceIdx);
                string data = this.GetData(sourceIdx);

                // Explicitly pass -1 here so InsertAt doesn't scramble the child table 
                // with automatic defaults while we're building the node
                destination.InsertAt(i, key, data, -1);
            }

            // 2. Cleanly migrate child pointers (Internal nodes only)
            if (!Leaf)
            {
                // Migrate exactly the active child pointers belonging to the transferred keys
                for (int i = 0; i <= count; i++)
                {
                    int sourceChildId = this.GetChildId(startIdx + i);
                    destination.SetChildId(i, sourceChildId);
                }

                // Initialize any remaining trailing sibling pointers to -1 to prevent zero leaks
                int maxPossibleChildren = destination.Order + 1;
                for (int i = count + 1; i < maxPossibleChildren; i++)
                {
                    destination.SetChildId(i, -1);
                }
            }
        }


        // ##########################################
        // # 9. DIAGNOSTICS & DISK I/O
        // ##########################################

        /// <summary> Reconstructs a BNode by reading a full page of raw bytes from disk and wrapping them in the Slotted-Page logic. </summary>
        public static BNode LoadFromStream(System.IO.BinaryReader reader, int pageSize, int order)
        {
            byte[] buffer = reader.ReadBytes(pageSize);
            if (buffer.Length < pageSize) throw new EndOfStreamException("Partial page read.");
            return new BNode(order, pageSize, buffer);
        }


        /// <summary> Generates a human-readable summary of the node's identity, type, and current list of logical keys. </summary>
        public override string ToString()
        {
            var keys = string.Join(", ", Enumerable.Range(0, NumKeys).Select(i => GetKey(i)));
            return $"Node {Id} (Leaf: {Leaf}, Keys: {NumKeys})\nKeys: [{keys}]\n";
        }


        // ##########################################
        // # 10. TEST FLUENCY & SHIMS
        // ##########################################

        /// <summary> Wraps the key and data at a specific index into a single Element object for easier manipulation in high-level logic. </summary>
        public Element GetElementFixed(int index) => new Element(GetKey(index), GetData(index));

        /// <summary> Iterates through the data heap to extract all active records into a standard list for bulk processing or validation. </summary>
        public List<Element> GetAllElements()
        {
            var list = new List<Element>(NumKeys); 
            for (int i = 0; i < NumKeys; i++)
            {
                list.Add(GetElementFixed(i));
            }
            return list;
        }

        /// <summary>
        /// Updates the record at the specified logical index using the key and payload from <paramref name="element"/>.
        /// </summary>
        public void SetElementFixed(int index, Element element) => SetElementFixed(index, element.Key, element.Data);

        /// <summary>
        /// Updates the record at the specified logical index with a new key and UTF-8 string payload,
        /// writing in place when smaller or equal and appending or compacting when growing.
        /// </summary>
        public void SetElementFixed(int index, int key, string data)
        {
            if (index < 0 || index >= NumKeys) throw new IndexOutOfRangeException();

            string value = data ?? string.Empty;
            int strByteCount = Encoding.UTF8.GetByteCount(value);
            int newRecordBytes = sizeof(int) + strByteCount;

            int currentOffset = GetDataOffset(index);
            int currentRecordBytes = GetDataLength(index);
            int slotAddress = SlotTableStart + (index * SLOT_SIZE);

            if (newRecordBytes <= currentRecordBytes)
            {
                // Fits in place (same size or shrinks)
                Span<byte> recordSpan = Buffer.AsSpan(currentOffset, newRecordBytes);
                BinaryPrimitives.WriteInt32LittleEndian(recordSpan.Slice(0, sizeof(int)), key);
                Encoding.UTF8.GetBytes(value, recordSpan.Slice(sizeof(int)));

                BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(slotAddress + 4, 4), newRecordBytes);

                int leftover = currentRecordBytes - newRecordBytes;
                if (leftover > 0)
                {
                    Buffer.AsSpan(currentOffset + newRecordBytes, leftover).Clear();
                }
                return;
            }

            // Grows larger: try placing at the end of the data heap, compacting first if needed
            int nextFreeOffset = GetNextFreeDataOffset();
            int minDataStart = SlotTableStart + (Order * SLOT_SIZE);

            if (nextFreeOffset + newRecordBytes > Buffer.Length || nextFreeOffset < minDataStart)
            {
                this.Compact();
                currentOffset = GetDataOffset(index);
                currentRecordBytes = GetDataLength(index);
                nextFreeOffset = GetNextFreeDataOffset();

                if (nextFreeOffset + newRecordBytes > Buffer.Length || nextFreeOffset < minDataStart)
                {
                    throw new InvalidOperationException($"Page {Id} does not have enough physical space to grow record at index {index}.");
                }
            }

            // Zero out old space before moving to the new tail offset
            Buffer.AsSpan(currentOffset, currentRecordBytes).Clear();

            Span<byte> newRecordSpan = Buffer.AsSpan(nextFreeOffset, newRecordBytes);
            BinaryPrimitives.WriteInt32LittleEndian(newRecordSpan.Slice(0, sizeof(int)), key);
            Encoding.UTF8.GetBytes(value, newRecordSpan.Slice(sizeof(int)));

            BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(slotAddress, 4), nextFreeOffset);
            BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan(slotAddress + 4, 4), newRecordBytes);
        }

        /// <summary> Fluent test helper that resets the node and populates it with a specific set of keys for rapid unit testing. </summary>
        public BNode WithKeys(params int[] keys)
        {
            this.NumKeys = 0;
            Buffer.AsSpan(DataAreaStart).Clear();
            Buffer.AsSpan(SlotTableStart, Order * SLOT_SIZE).Clear();

            for (int i = 0; i < keys.Length; i++)
            {
                InsertAt(i, keys[i], string.Empty);
            }

            return this;
        }


        /// <summary> Fluent test helper that quickly assigns a series of PageIds to the Kid Table for setting up internal node scenarios. </summary>
        public BNode WithChildren(params int[] childIds)
        {
            for (int i = 0; i < childIds.Length; i++) 
            { 
                SetChildId(i, childIds[i]); 
            }
            return this;
        }

        /// <summary> Fluent test helper that allows for setting the PageId in a single chained method call during node initialization. </summary>
        public BNode WithId(int id) 
        { 
            this.Id = id; 
            return this; 
        }
    }
}