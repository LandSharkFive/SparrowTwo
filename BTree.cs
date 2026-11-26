/*
 * ===========================================================================
 * MODULE:          B+ Tree Engine (SparrowTwo)
 * AUTHOR:          Koivu
 * DATE:            2026-11-25
 * VERSION:         2.0.0
 * LICENSE:         MIT License
 * ===========================================================================
 *
 ** ABSTRACT:
 * A disk-resident B+ Tree implementation with slotted-page storage for
 * variable-length UTF-8 payloads, single-pass rebalancing, leaf-chain
 * scans, free-list reclamation, and full compaction.
 *
 * ARCHITECTURAL SPECIFICATION:
 * - BALANCING:  Proactive top-down splits and single-pass borrow/merge on delete.
 * - PERSISTENCE: Slotted pages with 4-byte keys and variable-length UTF-8 strings.
 * - STORAGE:    FreeList page recycling plus full rebuild compaction.
 * - INTEGRITY:  Cycle, ghost, zombie, and structural integrity audits.
 *
 * KEYWORDS:
 * Balanced Tree, Disk-Resident, Slotted Pages, Variable-Length, Persistence.
 *
 * ---------------------------------------------------------------------------
 * Copyright (c) 2026 Koivu.
 * Licensed under the MIT License.
 * ---------------------------------------------------------------------------
 */


using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace SparrowTwo
{
    /// <summary>
    /// Provides a structured interface for B+ Tree file operations, 
    /// ensuring safe resource management and data persistence.
    /// </summary>
    public class BTree : IDisposable
    {
        private string MyFileName { get; set; }

        private FileStream MyFileStream;

        private BinaryReader MyReader;

        private BinaryWriter MyWriter;

        private readonly HashSet<int> FreeList = new HashSet<int>();

        private const int HeaderSize = 4096;

        private const int MagicConstant = BTreeHeader.MagicConstant;

        public BTreeHeader Header;

        public const int MINPAGESIZE = 256;

        public const int MINORDER = 4;


        /// <summary>
        /// Initializes a new instance of the BTree class by opening an existing file 
        /// or creating a new one with the specified page size.
        /// </summary>
        public BTree(string fileName, int pageSize = 4096)
        {
            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentException("File name is empty.");

            if (pageSize < MINPAGESIZE)
                throw new ArgumentException($"Page size must be at least {MINPAGESIZE} bytes.");

            MyFileName = fileName;

            OpenStorage();

            if (MyFileStream.Length > 0)
            {
                // 2. Existing file: Trust the disk.
                LoadHeader();
                LoadFreeList();
            }
            else
            {
                // 3. New file.
                Header.Initialize(pageSize);
                SaveHeader();
            }
        }

        /// <summary>
        /// Open the file stream. 
        /// </summary>
        private void OpenStorage()
        {
            const int BufferSize = 65536;

            // 1. Close existing file.
            MyWriter = null;
            MyReader = null;
            MyFileStream = null;

            // 2. Open the file.
            MyFileStream = new FileStream(MyFileName, FileMode.OpenOrCreate,
                                          FileAccess.ReadWrite, FileShare.None, BufferSize);

            // 3. Create the reader and writer.
            MyReader = new BinaryReader(MyFileStream, System.Text.Encoding.UTF8, true);
            MyWriter = new BinaryWriter(MyFileStream, System.Text.Encoding.UTF8, true);
        }

        /// <summary>
        /// Close the file stream.
        /// </summary>
        private void CloseStorage()
        {
            MyWriter?.Dispose();
            MyReader?.Dispose();
            MyFileStream?.Dispose();

            MyWriter = null;
            MyReader = null;
            MyFileStream = null;
        }


        // ------ HELPER METHODS ------


        /// <summary>
        /// Calculates the byte offset in the file for a given disk position (record index).
        /// </summary>
        private long CalculateOffset(int id)
        {
            if (id < 0)
                throw new ArgumentOutOfRangeException("Page Id must be 0 or positive.");

            if (Header.PageSize < MINPAGESIZE)
                throw new ArgumentException("Page Size too small.");

            return ((long)Header.PageSize * id) + HeaderSize;
        }

        /// <summary>
        /// Closes the object and releases resources by calling the Dispose method.
        /// </summary>
        public void Close()
        {
            Dispose();
        }

        /// <summary>
        /// Safely persists data and releases the file stream while preventing redundant cleanup.
        /// </summary>
        public void Dispose()
        {
            if (MyFileStream != null)
            {
                try
                {
                    SaveFreeList();
                    SaveHeader();
                }
                finally
                {
                    CloseStorage();
                }
            }
            // Tell the GC we've already handled the cleanup.
            GC.SuppressFinalize(this);
        }

        // -------- DISK I/O METHODS -----------

        /// <summary>
        /// Completely wipes the B+ Tree structure and truncates the underlying data file.
        /// This resets the header, clears the free list, and prepares the file for a fresh bulk load.
        /// </summary>
        /// <remarks>
        /// Warning: This operation is destructive and cannot be undone.
        /// </remarks>
        public void Clear()
        {
            // 1. Wipe the physical file
            MyFileStream.SetLength(0);
            MyFileStream.Flush();
            MyFileStream.Seek(0, SeekOrigin.Begin); // Crucial: Reset the pointer

            // 2. Reset the logical state
            Header.RootId = -1;
            Header.NodeCount = 0;
            FreeList.Clear();

            // 3. Re-initialize the Header in the file
            SaveHeader();
        }

        /// <summary>
        /// Hydrates a BNode from the physical disk by seeking to its page-aligned offset.
        /// This is the primary I/O gateway for tree traversal.
        /// </summary>
        public BNode DiskRead(int id)
        {
            if (id < 0) throw new IndexOutOfRangeException("Page Id must be 0 or positive.");

            MyFileStream.Seek(CalculateOffset(id), SeekOrigin.Begin);

            BNode node = BNode.LoadFromStream(MyReader, Header.PageSize, Header.Order);

            if (node.Id != id)
                throw new Exception($"Corruption: Expected Node {id}, but read Node {node.Id}.");

            return node;
        }

        /// <summary>
        /// Persists a BNode to physical storage using the page-aligned binary layout.
        /// This is the inverse of DiskRead and must maintain strict layout compatibility.
        /// </summary>
        public void DiskWrite(BNode node)
        {
            if (node.Id < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(node.Id), "Page Id must be 0 or positive.");
            }

            // 1. Calculate the physical offset in the file
            // Node 0 is at offset 0, Node 1 is at offset PageSize, etc.
            long physicalOffset = CalculateOffset(node.Id);

            // 2. Move the stream pointer
            MyFileStream.Seek(physicalOffset, SeekOrigin.Begin);

            // 3. Write the entire buffer in one shot
            // Since BNode properties write directly to this buffer, it's already up to date.
            MyFileStream.Write(node.GetBuffer(), 0, node.PageSize);
        }


        /// <summary>
        /// Wipes a specific node's data on disk by overwriting its sector with zeros.
        /// This is typically used for security or to clean up nodes moved to a free list.
        /// </summary>
        public void ZeroNode(int id)
        {
            if (id < 0) throw new ArgumentOutOfRangeException("Page Id must be 0 or positive.");

            // 1. Create buffer on the stack.
            Span<byte> buffer = stackalloc byte[Header.PageSize];
            buffer.Clear();

            // 2. Physical write.
            long offset = CalculateOffset(id);
            MyFileStream.Seek(offset, SeekOrigin.Begin);
            MyFileStream.Write(buffer);
        }


        /// <summary>
        /// Synchronizes the internal file stream with the underlying storage device to ensure all changes are persisted.
        /// </summary>
        public void Commit()
        {
            if (MyFileStream != null && MyFileStream.CanWrite)
            {
                SaveHeader();
                MyFileStream.Flush();
            }
        }

        // --- HEADER METHODS ---

        /// <summary>
        /// Writes the B+ Tree header to disk.
        /// </summary>
        public void SaveHeader()
        {
            MyFileStream.Seek(0, SeekOrigin.Begin);
            Header.Write(MyWriter);
        }

        /// <summary>
        /// Load the B+ Tree header from disk.
        /// </summary>
        public void LoadHeader()
        {
            MyFileStream.Seek(0, SeekOrigin.Begin);
            Header = BTreeHeader.Read(MyReader);

            if (Header.Magic != MagicConstant)
                throw new InvalidDataException("Invalid File Format");

            if (Header.PageSize < MINPAGESIZE)
                throw new ArgumentException($"Page Size must be at least {MINPAGESIZE}.");

            if (Header.Order < MINORDER)
                throw new ArgumentException($"Order must be at least {MINORDER}.");
        }


        // --- SEARCH METHODS ---

        /// <summary>
        /// Performs a hierarchical descent from Root to Leaf to find a specific key.
        /// Uses linear scanning for internal navigation and binary search for leaf-level precision.
        /// </summary>
        public bool TrySearch(int key, out Element result)
        {
            result = Element.GetDefault();
            if (Header.RootId == -1) return false;

            BNode node = DiskRead(Header.RootId);

            // Phase 1: Descend to the Leaf using linear scan.
            while (!node.Leaf)
            {
                int i = 0;
                while (i < node.NumKeys && key >= node.GetKey(i))
                {
                    i++;
                }

                int nextId = node.GetChildId(i);

                // --- THE SAFETY CHECK ---
                // FATAL: Internal nodes must always have valid child pointers. 
                // A -1 here means the "Kid Table" is corrupted or truncated.
                if (nextId == -1)
                    throw new ArgumentException($"Corrupted: Node {node.Id} points to -1 at index {i}.");

                node = DiskRead(nextId);
            }

            // Phase 2: Binary Search ONLY in the Leaf
            int low = 0, high = node.NumKeys - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                int midKey = node.GetKey(mid);

                if (midKey == key)
                {
                    result = node.GetElementFixed(mid);
                    return true;
                }

                if (midKey < key) low = mid + 1;
                else high = mid - 1;
            }

            return false;
        }


        /// <summary>
        /// Retrieves the very first (minimum) element in the B+ Tree.
        /// It traverses the leftmost path of index nodes until it reaches the first leaf node,
        /// where the smallest key is stored in the first position.
        /// </summary>
        public Element SelectFirst()
        {
            if (Header.RootId == -1) return default;

            int currentId = Header.RootId;
            while (true)
            {
                BNode node = DiskRead(currentId);
                if (node.Leaf)
                {
                    return node.NumKeys > 0 ? node.GetElementFixed(0) : default;
                }
                // Always go down the very first child pointer
                currentId = node.GetChildId(0);
            }
        }

        /// <summary>
        /// Retrieves the very last (maximum) element in the B+ Tree.
        /// It traverses the rightmost path of index nodes until it reaches the last leaf node,
        /// where the largest key is stored at the final active index.
        /// </summary>
        public Element SelectLast()
        {
            if (Header.RootId == -1) return default;

            int currentId = Header.RootId;
            while (true)
            {
                BNode node = DiskRead(currentId);
                if (node.Leaf)
                {
                    return node.NumKeys > 0 ? node.GetElementFixed(node.NumKeys - 1) : default;
                }
                // Always go down the last active child pointer
                currentId = node.GetChildId(node.NumKeys);
            }
        }

        /// <summary>
        /// Streams elements within a specified range [startKey, endKey] by locating 
        /// the starting leaf and following horizontal sibling links (NextLeafId).
        /// </summary>
        public IEnumerable<Element> EnumerateRange(int startKey, int endKey)
        {
            // FindLeaf likely reuses your TrySearch descent logic to find the first page.
            BNode current = FindLeaf(Header.RootId, startKey);

            while (current != null)
            {
                for (int i = 0; i < current.NumKeys; i++)
                {
                    // Retrieve elements directly from the slotted data area using physical offsets.
                    Element element = current.GetElementFixed(i);

                    if (element.Key > endKey)
                        yield break; // We've moved past the target range; terminate.

                    if (element.Key >= startKey)
                        yield return element;
                }

                // Horizontal Linkage: This is where B+ Trees beat standard B-Trees for range scans.
                if (current.NextLeafId == -1) break;
                current = DiskRead(current.NextLeafId);
            }
        }

        /// <summary> Extracts a sorted list of unique keys within the specified range from the tree. </summary>
        public List<int> GetKeyRange(int startKey, int endKey)
            => EnumerateRange(startKey, endKey).Select(e => e.Key).ToList();

        /// <summary> Materializes the full key-value pairs within the specified range into a memory-resident list. </summary>
        public List<Element> GetElementRange(int startKey, int endKey)
            => EnumerateRange(startKey, endKey).ToList();


        /// <summary>
        /// Traverses internal nodes using binary search to locate 
        /// the leaf node that should contain the specified key.
        /// </summary>
        public BNode FindLeaf(int nodeId, int key)
        {
            if (nodeId == -1) return null;

            int currentId = nodeId;
            while (true)
            {
                BNode node = DiskRead(currentId);
                if (node.Leaf) return node;

                // Use the new helper method instead of manual binary search
                int low = node.FindChildBranchIndex(key);
                currentId = node.GetChildId(low);
            }
        }

        // ------- INSERT METHODS --------


        /// <summary>Inserts a new Element into the collection using the specified key and data.</summary>
        public void Insert(Element item)
        {
            Insert(item.Key, item.Data);
        }

        /// <summary>
        /// Inserts an element into the B+ Tree. If the tree is empty, it initializes the root. 
        /// If the root is full, it performs a preemptive split to increase tree height 
        /// before delegating to the recursive insertion logic.
        /// </summary>
        public void Insert(int key, string data)
        {
            // 1. Does key already exist?
            Element item;
            if (TrySearch(key, out item))
            {
                // Standard Map behavior: Update existing instead of creating a duplicate.
                this.UpdateValue(key, data);
                return;
            }

            bool headerChanged = false;

            // 2. Handle Empty Tree.
            if (Header.RootId == -1)
            {
                BNode firstNode = new BNode(Header.Order, Header.PageSize, true, GetNextId());
                Header.RootId = firstNode.Id;
                firstNode.InsertAt(0, key, data);

                DiskWrite(firstNode);
                headerChanged = true;
            }
            else
            {
                BNode rootNode = DiskRead(Header.RootId);

                // MODIFIED: Use the physical IsFull() guard instead of logical Header.Order check
                if (rootNode.IsFull(data))
                {
                    // 1. Create the new root (Internal)
                    BNode newRoot = new BNode(Header.Order, Header.PageSize, false, GetNextId());
                    newRoot.SetChildId(0, rootNode.Id);

                    // 2. Perform the split
                    var (newSibling, separatorKey) = rootNode.Split(GetNextId());

                    // 3. Connect the new sibling and push up the key
                    newRoot.InsertAt(0, separatorKey, string.Empty, newSibling.Id);

                    // 4. PERSIST changes before descending
                    DiskWrite(rootNode);
                    DiskWrite(newSibling);
                    DiskWrite(newRoot);

                    Header.RootId = newRoot.Id;
                    headerChanged = true;

                    // 5. Decide which branch to take based on the split boundary
                    if (key < separatorKey)
                    {
                        InsertNonFull(rootNode, key, data);
                    }
                    else
                    {
                        InsertNonFull(newSibling, key, data);
                    }
                }
                else
                {
                    InsertNonFull(rootNode, key, data);
                }
            }

            // 4. Save the Header if the RootId changed
            if (headerChanged)
            {
                SaveHeader();
            }
        }


        // --- INSERTION HELPERS ---

        /// <summary>
        /// Performs a single-pass insertion by preemptively splitting full children 
        /// during the descent. This ensures that the parent node always has space 
        /// to accommodate a potential split from below.
        /// </summary>
        private void InsertNonFull(BNode node, int key, string data)
        {
            int pos = node.FindInsertionPoint(key);

            if (node.Leaf)
            {
                node.InsertAt(pos, key, data);
                DiskWrite(node);
            }
            else
            {
                int childId = node.GetChildId(pos);
                BNode child = DiskRead(childId);

                if (child.IsFull(data))
                {
                    var (newSibling, separatorKey) = child.Split(GetNextId());

                    // 1. RE-CALCULATE pos specifically for the separator
                    int separatorPos = node.FindInsertionPoint(separatorKey);
                    node.InsertAt(separatorPos, separatorKey, string.Empty, newSibling.Id);

                    DiskWrite(child);
                    DiskWrite(newSibling);
                    DiskWrite(node);

                    // 2. RE-CALCULATE pos for the original key we are trying to insert
                    // The split changed the parent's boundaries!
                    pos = node.FindInsertionPoint(key);

                    // 3. Update our 'child' pointer to the correct branch
                    int newChildId = node.GetChildId(pos);
                    if (newChildId == newSibling.Id)
                    {
                        child = newSibling;
                    }
                    // If it's still the original childId, we don't need to do anything;
                    // 'child' is already pointing to the correct node.
                }

                InsertNonFull(child, key, data);
            }
        }



        /// <summary>
        /// Returns the total number of nodes currently allocated in the B+ Tree.
        /// </summary>
        public int GetNodeCount()
        {
            return Header.NodeCount;
        }

        /// <summary>
        /// Provides a node ID for a new allocation by recycling an ID from the FreeList 
        /// or, if none are available, appending a new ID at the end of the storage.
        /// </summary>
        public int GetNextId()
        {
            // Get first item.
            using (var enumerator = FreeList.GetEnumerator())
            {
                if (enumerator.MoveNext())
                {
                    int nodeId = enumerator.Current;
                    FreeList.Remove(nodeId);
                    return nodeId;
                }
            }

            // Append to end of file.
            int nextPos = Header.NodeCount;
            Header.NodeCount++;
            return nextPos;
        }

        /// <summary>
        /// Updates the data associated with an existing key. 
        /// In a B+ Tree architecture, actual data values are stored exclusively in leaf nodes. 
        /// This method traverses the index to the correct leaf, performs a binary search 
        /// to find the key, and persists the modified data element back to disk.
        /// </summary>
        public bool UpdateValue(int key, string data)
        {
            // 1. Find the leaf
            BNode leaf = FindLeaf(Header.RootId, key);

            // Safety check: ensure we actually have a leaf and not an internal node
            if (leaf == null || !leaf.Leaf) return false;

            // 2. If the leaf is already full and the new payload may grow, split proactively.
            if (leaf.IsFull(data))
            {
                Insert(key, data);
                return true;
            }

            // 3. Binary search
            int low = 0, high = leaf.NumKeys - 1;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;

                if (leaf.GetKey(mid) == key)
                {
                    try
                    {
                        leaf.SetElementFixed(mid, key, data);
                        DiskWrite(leaf);
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        // Leaf still couldn't fit the grown string even after compaction:
                        // remove the existing entry and re-insert so top-down splitting can run.
                        leaf.RemoveKeyAndChildAt(mid);
                        DiskWrite(leaf);
                        Insert(key, data);
                        return true;
                    }
                }

                if (key < leaf.GetKey(mid)) high = mid - 1;
                else low = mid + 1;
            }

            return false;
        }

        /// <summary>
        /// AddOrUpdates an element into the B+ Tree. It first attempts to locate the key 
        /// at the leaf level to update its value. If the key is not found, it performs 
        /// a standard B+ Tree insertion, which may involve splitting nodes from the 
        /// root down to the leaves to accommodate the new record.
        /// </summary>
        public void AddOrUpdate(int key, string data)
        {
            // Try to update; if it fails, the key doesn't exist, so insert.
            if (!UpdateValue(key, data))
            {
                Insert(new Element { Key = key, Data = data });
            }
        }

        /// <summary>
        /// Adds a new element or updates an existing one using an Element object.
        /// This ensures the B+ Tree remains the single source of truth for the 
        /// data record associated with the element's key.
        /// </summary>
        public void AddOrUpdate(Element item)
        {
            AddOrUpdate(item.Key, item.Data);
        }

        /// <summary>
        /// Updates an existing record's data using the key provided in the Element object.
        /// Returns true if the key was found and updated in a leaf node; otherwise, false.
        /// </summary>
        public bool UpdateValue(Element item)
        {
            return UpdateValue(item.Key, item.Data);
        }


        // ------ DELETE METHODS ------

        /// <summary>
        /// Implements single-pass deletion by ensuring child nodes meet minimum occupancy 
        /// during descent to prevent backtracking. Performs a "Root Collapse" if the 
        /// tree height decreases after the operation.
        /// </summary>
        public void Delete(int key)
        {
            if (Header.RootId == -1) return;

            Element deleteKey = new Element(key, String.Empty);
            BNode rootNode = DiskRead(Header.RootId);

            // 1. Perform the recursive deletion
            DeleteSafe(rootNode, deleteKey);

            // 2. IMPORTANT: Persist any changes made to the rootNode during recursion
            DiskWrite(rootNode);

            // 3. RE-READ to ensure we are looking at the absolute latest state
            BNode finalRoot = DiskRead(Header.RootId);

            // 4. FIXED: Loop to handle multi-level or recursive collapses down to the final leaf
            while (finalRoot.NumKeys == 0 && !finalRoot.Leaf)
            {
                int oldId = Header.RootId;

                // Promote the first child to be the new Root.
                Header.RootId = finalRoot.GetChildId(0);

                // Save the Header immediately so the Audit knows where to start.
                SaveHeader();

                // Clean up the evidence of the old root
                FreeNode(oldId);

                // Re-read the newly promoted root to see if it needs another collapse pass
                finalRoot = DiskRead(Header.RootId);
            }
        }


        // ------ DELETE HELPERS -------

        /// <summary>
        /// Locates and removes the absolute minimum element in the B+ Tree.
        /// It traverses the leftmost edge of the index nodes to reach the first leaf node 
        /// in the linked chain, then triggers a deletion for the first key found there.
        /// This may cause a ripple effect of merges or key redistributions up the tree 
        /// to satisfy B+ Tree occupancy requirements.
        /// </summary>
        public void DeleteFirst()
        {
            if (Header.RootId == -1) return;

            // 1. Travel down the "Leftmost" path to the leaf
            BNode current = DiskRead(Header.RootId);
            while (!current.Leaf)
            {
                current = DiskRead(current.GetChildId(0));
            }

            // 2. The first key in that leaf is the winner (or loser)
            if (current.NumKeys > 0)
            {
                var item = current.GetElementFixed(0);
                Delete(item.Key);
            }
        }

        /// <summary>
        /// Locates and removes the absolute maximum element in the B+ Tree.
        /// It traverses the rightmost path of the index nodes, always following the 
        /// last active child pointer, until it reaches the final leaf node. 
        /// The last key in this leaf is the maximum value, which is then passed to 
        /// the Delete logic for removal and potential structural rebalancing.
        /// </summary>
        public void DeleteLast()
        {
            if (Header.RootId == -1) return;

            // 1. Travel down the "Rightmost" path
            BNode current = DiskRead(Header.RootId);
            while (!current.Leaf)
            {
                current = DiskRead(current.GetChildId(current.NumKeys));
            }

            // 2. The last key in that leaf is the target
            if (current.NumKeys > 0)
            {
                var item = current.GetElementFixed(current.NumKeys - 1);
                Delete(item.Key);
            }
        }


        /// <summary>
        /// Merges two siblings by pulling the parent's separator down into the left child (Y) 
        /// and appending all elements/children from the right child (Z). 
        /// Decommissions Z and updates the leaf linked list if applicable.
        /// </summary>
        private bool MergeChildren(BNode parent, int pos, BNode y, BNode z)
        {
            if (!CanMergeIntoLeft(parent, pos, y, z))
            {
                // Leave both nodes intact when variable-length payloads exceed single-page byte capacity
                return false;
            }

            if (!y.Leaf)
            {
                // 1. Internal Merge: Pull parent separator down
                // Insert at the end of Y. Pass Z's first child pointer as the right child of this key.
                y.InsertAt(y.NumKeys, parent.GetKey(pos), parent.GetData(pos), z.GetChildId(0));

                // 2. Append Z's elements and children
                int zCount = z.NumKeys;
                for (int i = 0; i < zCount; i++)
                {
                    // Insert sequentially at the end of Y, pulling child pointers along
                    y.InsertAt(y.NumKeys, z.GetKey(i), z.GetData(i), z.GetChildId(i + 1));
                }
            }
            else
            {
                // 3. Leaf Merge: Transfer all and update linked list
                for (int i = 0; i < z.NumKeys; i++)
                {
                    // Leaf nodes don't have children, so childPageId defaults to -1
                    y.InsertAt(y.NumKeys, z.GetKey(i), z.GetData(i));
                }
                y.NextLeafId = z.NextLeafId;
            }

            // 4. Collapse Parent and Persist
            parent.RemoveKeyAndChildAt(pos);

            DiskWrite(y);
            DiskWrite(parent);
            FreeNode(z.Id); // Immediately recycle the page ID
            return true;
        }

        /// <summary>
        /// Checks whether merging sibling node <paramref name="z"/> (plus the parent separator key for
        /// internal nodes) into left sibling <paramref name="y"/> stays within <see cref="BNode.Order"/>
        /// and the physical byte capacity of a single page.
        /// </summary>
        private bool CanMergeIntoLeft(BNode parent, int pos, BNode y, BNode z)
        {
            int headerAndKids = 16 + ((y.Order + 1) * 4); // HEADER_SIZE + KidTable
            int slotTableBytes = y.Order * 8;            // SLOT_SIZE = 8
            int dataAreaStart = headerAndKids + slotTableBytes;

            int existingPayloadBytes = 0;
            for (int i = 0; i < y.NumKeys; i++)
            {
                existingPayloadBytes += y.GetDataLength(i);
            }

            int incomingPayloadBytes = 0;
            if (!y.Leaf)
            {
                string sepData = parent.GetData(pos) ?? string.Empty;
                incomingPayloadBytes += sizeof(int) + Encoding.UTF8.GetByteCount(sepData);
            }

            for (int i = 0; i < z.NumKeys; i++)
            {
                string zData = z.GetData(i) ?? string.Empty;
                incomingPayloadBytes += sizeof(int) + Encoding.UTF8.GetByteCount(zData);
            }

            int totalKeysAfterMerge = y.NumKeys + z.NumKeys + (y.Leaf ? 0 : 1);
            if (totalKeysAfterMerge > y.Order) return false;

            int requiredEndOffset = dataAreaStart + existingPayloadBytes + incomingPayloadBytes;
            return requiredEndOffset <= y.PageSize;
        }


        /// <summary>
        /// Performs a right-rotation by borrowing the tail element from the left sibling.
        /// For internal nodes, the parent's separator moves down to the child's head, 
        /// and the sibling's last child becomes the child's new first child.
        /// For leaf nodes, the sibling's last element moves directly to the child's head.
        /// </summary>
        private void BorrowFromLeftSibling(BNode parent, int pos, BNode child, BNode leftSibling)
        {
            // STEP 1: Capture the element to be borrowed from the sibling's TAIL
            int lastIdx = leftSibling.NumKeys - 1;
            Element borrowedElement = leftSibling.GetElementFixed(lastIdx);

            int keyToInsert;
            string dataToInsert = borrowedElement.Data;
            int childIdToInsert = -1;

            if (child.Leaf)
            {
                // LEAF: Sibling's last element moves to index 0 directly
                keyToInsert = borrowedElement.Key;
            }
            else
            {
                // Internal nodes rotate through the parent separator
                Element parentSeparator = parent.GetElementFixed(pos - 1);
                keyToInsert = parentSeparator.Key;
                dataToInsert = parentSeparator.Data; // Keep parent data payload matching context

                // Sibling's last child moves over to become child's first child
                childIdToInsert = leftSibling.GetChildId(lastIdx + 1);

                // Sibling's last key moves up to parent separator position
                parent.SetElementFixed(pos - 1, borrowedElement);
            }

            // STEP 2: Safe Insert & Remove (Handles Slotted Architecture & High-level counters)
            // InsertAt shifts slot directory right, writes to free heap, and shifts children if internal.
            if (child.Leaf)
            {
                child.InsertAt(0, keyToInsert, dataToInsert, -1);
            }
            else
            {
                // InsertAt puts childIdToInsert at pos + 1 (index 1), so preserve existing Child[0] there
                // and then place the borrowed child pointer into Child[0].
                int oldFirstChild = child.GetChildId(0);
                child.InsertAt(0, keyToInsert, dataToInsert, oldFirstChild);
                child.SetChildId(0, childIdToInsert);
            }

            // RemoveKeyAndChildAt shifts the left sibling's slot directory left cleanly.
            leftSibling.RemoveKeyAndChildAt(lastIdx);

            // STEP 3: Parent separator update (Only needed for leaf mutations)
            if (child.Leaf)
            {
                var leftSeparator = child.GetElementFixed(0);
                parent.SetElementFixed(pos - 1, leftSeparator.Key, string.Empty);
            }

            // STEP 4: Persist changes
            DiskWrite(child);
            DiskWrite(leftSibling);
            DiskWrite(parent);
        }



        /// <summary>
        /// Performs a left-rotation by borrowing the head element from the right sibling.
        /// For internal nodes, the parent's separator moves down to the child's tail,
        /// and the sibling's first child becomes the child's new last child.
        /// For leaf nodes, the sibling's first element moves to the child's tail and 
        /// the parent's separator is updated to the sibling's new head.
        /// </summary>
        private void BorrowFromRightSibling(BNode parent, int pos, BNode child, BNode rightSibling)
        {
            // STEP 1: Capture the right sibling's boundary element before modifying anything
            Element siblingFirstElement = rightSibling.GetElementFixed(0);

            int keyToInsert;
            string dataToInsert;
            int childIdToInsert;

            if (child.Leaf)
            {
                // LEAF NODE LOGIC
                // Sibling's first element moves completely to the child's tail.
                keyToInsert = siblingFirstElement.Key;
                dataToInsert = siblingFirstElement.Data;
                childIdToInsert = -1; // Leaves don't have children
            }
            else
            {
                // INTERNAL NODE LOGIC
                Element parentSeparator = parent.GetElementFixed(pos);

                // The parent's divider key moves down to the child's tail.
                keyToInsert = parentSeparator.Key;
                dataToInsert = parentSeparator.Data;

                // The sibling's leftmost child moves to become the child's rightmost child.
                childIdToInsert = rightSibling.GetChildId(0);

                // The sibling's first key moves up to the parent, maintaining the parent's structural tracking.
                parent.SetElementFixed(pos, siblingFirstElement.Key, parentSeparator.Data);
            }

            // STEP 2: Execute Mutations safely using Slotted Page helpers
            child.InsertAt(child.NumKeys, keyToInsert, dataToInsert, childIdToInsert);

            if (child.Leaf)
            {
                // Leaf nodes use standard index-0 removal.
                rightSibling.RemoveKeyAndChildAt(0);
            }
            else
            {
                // Internal nodes use the specialized left-child-dropping helper.
                rightSibling.RemoveFirstKeyAndLeftChildInternal();
            }

            // STEP 3: Update parent separator for Leaf nodes
            if (child.Leaf)
            {
                // The NEW boundary key for the parent must reflect the right sibling's updated head element
                var newSeparatorForParent = rightSibling.GetElementFixed(0);
                parent.SetElementFixed(pos, newSeparatorForParent.Key, string.Empty);
            }

            // STEP 4: Persist changes
            DiskWrite(child);
            DiskWrite(rightSibling);
            DiskWrite(parent);
        }


        /// <summary>
        /// Recursively traverses the tree to locate and remove a key from a leaf node. 
        /// Employs a single-pass strategy by ensuring child nodes meet minimum 
        /// occupancy requirements before descent.
        /// </summary>
        private void DeleteSafe(BNode node, Element target)
        {
            if (node.Leaf)
            {
                int lowIdx = 0, highIdx = node.NumKeys - 1;
                while (lowIdx <= highIdx)
                {
                    int mid = lowIdx + ((highIdx - lowIdx) >> 1);
                    int midKey = node.GetKey(mid);

                    if (midKey == target.Key)
                    {
                        node.RemoveKeyAndChildAt(mid);
                        DiskWrite(node);
                        return;
                    }

                    if (midKey < target.Key) lowIdx = mid + 1;
                    else highIdx = mid - 1;
                }
                return;
            }

            int pos = node.FindChildBranchIndex(target.Key);
            int childPageId = node.GetChildId(pos);
            BNode child = DiskRead(childPageId);

            int t = (Header.Order + 1) / 2;
            if (child.NumKeys < t)
            {
                child = PrepareChildForDeletion(node, pos);
            }

            DeleteSafe(child, target);
        }


        /// <summary>
        /// Ensures that a child node has enough keys (at least t) before the deletion process 
        /// descends into it. In a B+ Tree, if a node is under-full, this method attempts to 
        /// restore balance by borrowing a key from a sibling or merging two siblings together.
        /// This proactive approach prevents the need for multiple passes or "fixing" the tree 
        /// after the deletion is already complete.
        /// </summary>
        private BNode PrepareChildForDeletion(BNode parent, int pos)
        {
            // t is the minimum occupancy (usually half the order)
            int t = (Header.Order + 1) / 2;
            BNode child = DiskRead(parent.GetChildId(pos));

            // If child is already robust enough, just keep going
            if (child.NumKeys >= t) return child;

            // 1. Try Borrow Left
            if (pos > 0)
            {
                BNode left = DiskRead(parent.GetChildId(pos - 1));
                if (left.NumKeys >= t)
                {
                    // OPTIMIZATION: Instead of re-reading from disk,
                    // we use the 'child' we already have, which the borrow method updates.
                    BorrowFromLeftSibling(parent, pos, child, left);
                    return child;
                }
            }

            // 2. Try Borrow Right
            if (pos < parent.NumKeys)
            {
                BNode right = DiskRead(parent.GetChildId(pos + 1));
                if (right.NumKeys >= t)
                {
                    BorrowFromRightSibling(parent, pos, child, right);
                    return child;
                }
            }

            // 3. Must Merge
            // If we merge, child 'y' absorbs sibling 'z'. 'y' is still the same object.
            int mergeIdx = (pos < parent.NumKeys) ? pos : pos - 1;

            // We need the nodes involved in the merge
            BNode leftForMerge = (pos < parent.NumKeys) ? child : DiskRead(parent.GetChildId(pos - 1));
            BNode rightForMerge = (pos < parent.NumKeys) ? DiskRead(parent.GetChildId(pos + 1)) : child;

            bool merged = MergeChildren(parent, mergeIdx, leftForMerge, rightForMerge);

            if (!merged)
            {
                return child;
            }

            DiskWrite(parent);       // <-- CRITICAL: Save the parent's new child pointer table layout!
            DiskWrite(leftForMerge); // <-- CRITICAL: Save the merged keys!
            // The left node now contains all data. Return that in-memory object.

            return leftForMerge;
        }

        /// ------- FREE LIST -------

        /// <summary>
        /// Reclaims a decommissioned node's ID by adding it to the pool of available addresses for future allocation.
        /// </summary>
        public void FreeNode(int id)
        {
            if (id < 0) return;
            FreeList.Add(id);
            ZeroNode(id);
        }

        /// <summary>
        /// Returns the total number of recycled node slots currently available for reuse.
        /// </summary>
        public int GetFreeListCount()
        {
            return FreeList.Count;
        }


        /// <summary>
        /// Persist the in-memory free list to the tail of the file and record its offset/count in the header.
        /// </summary>
        private void SaveFreeList()
        {
            if (FreeList.Count == 0) return;

            Header.FreeListCount = FreeList.Count;

            // 1. Move to the end of the file
            long offset = MyWriter.BaseStream.Length;
            MyWriter.BaseStream.Seek(offset, SeekOrigin.Begin);
            Header.FreeListOffset = offset;

            // 2. Prepare the buffer
            int totalBytes = FreeList.Count * 4;

            // 3. Use ArrayPool for large lists to avoid StackOverflow.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
            Span<byte> span = buffer.AsSpan(0, totalBytes);

            try
            {
                int currentOffset = 0;
                foreach (int id in FreeList)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(span.Slice(currentOffset, 4), id);
                    currentOffset += 4;
                }

                // 4. One single high-speed write to disk
                MyWriter.Write(span);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

        }


        /// <summary>
        /// Load the free list from disk into memory.
        /// On open, LoadFreeList reads the free list and truncates the file tail to reclaim space.
        /// </summary>
        private void LoadFreeList()
        {
            if (Header.FreeListOffset == 0 || Header.FreeListCount == 0) return;

            // 1. Jump to the list
            MyReader.BaseStream.Seek(Header.FreeListOffset, SeekOrigin.Begin);
            FreeList.Clear();

            // 2. Bulk Read
            int totalBytes = Header.FreeListCount * 4;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
            Span<byte> span = buffer.AsSpan(0, totalBytes);

            try
            {
                MyReader.BaseStream.ReadExactly(span);

                for (int i = 0; i < Header.FreeListCount; i++)
                {
                    // Slice into the buffer 4 bytes at a time
                    int nodeId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i * 4, 4));
                    FreeList.Add(nodeId);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // 3. TRUNCATE
            MyFileStream.SetLength(Header.FreeListOffset);

            Header.FreeListCount = 0;
            Header.FreeListOffset = 0;
            SaveHeader();
        }

        /// <summary>
        /// Displays the first 50 available IDs in the FreeList, used for tracking recycled disk space.
        /// </summary>
        public void PrintFreeList()
        {
            var ids = FreeList.Take(50);
            Console.WriteLine($"FreeList: {string.Join(" ", ids)}");
        }

        /// ------- COMPACT METHODS -------

        /// <summary>
        /// Rebuilds the tree into a temporary file to eliminate fragmentation and reclaim space.
        /// Uses a BitArray to identify live nodes and an ID map to maintain structural integrity 
        /// via an atomic file swap.
        /// </summary>
        public void Compact()
        {
            if (Header.RootId == -1) return;

            string tempPath = MyFileName + ".tmp";

            if (File.Exists(tempPath)) File.Delete(tempPath);

            // 1. Identify live nodes
            BitArray liveNodes = new BitArray(Header.NodeCount + 1);
            FindLiveNodes(Header.RootId, liveNodes);

            // 2. O(1) Mapping Array
            int[] idMap = new int[Header.NodeCount + 1];
            Array.Fill(idMap, -1);

            int nextId = 0;
            for (int i = 0; i < liveNodes.Count; i++)
            {
                if (liveNodes.Get(i)) idMap[i] = nextId++;
            }

            // 3. Rebuild in temp file
            using (var newTree = new BTree(tempPath, Header.PageSize))
            {
                for (int oldId = 0; oldId < idMap.Length; oldId++)
                {
                    if (idMap[oldId] == -1) continue;

                    BNode node = DiskRead(oldId);
                    node.Id = idMap[oldId];

                    if (!node.Leaf)
                    {
                        for (int i = 0; i <= node.NumKeys; i++)
                        {
                            if (node.GetChildId(i) != -1)
                                node.SetChildId(i, idMap[node.GetChildId(i)]);
                        }
                    }
                    else if (node.NextLeafId != -1)
                    {
                        // Remap the horizontal link
                        node.NextLeafId = idMap[node.NextLeafId];
                    }

                    newTree.DiskWrite(node);
                }

                // Update the new header
                newTree.Header.RootId = idMap[Header.RootId];
                newTree.Header.NodeCount = nextId;
                // If your property is named differently, use your 'GetNextId' source here
                newTree.SaveHeader();
            }

            // 4. Swap
            CloseStorage();

            // 5. Overwrite file.
            File.Delete(MyFileName);
            File.Move(tempPath, MyFileName, overwrite: true);

            // 6. Re-establish connection
            OpenStorage();
            LoadHeader();

            // 7. FreeList is now logically empty because we only kept 'Live' nodes.
            FreeList.Clear();
            Header.FreeListCount = 0;
            Header.FreeListOffset = 0;
            SaveHeader();
        }


        /// <summary>
        /// Check for valid node offset to prevent EndOfStreamException.
        /// </summary>
        private bool IsValidNodeOffset(int nodeId)
        {
            if (nodeId < 0) return false;

            // Calculate expected offset: HeaderSize + (nodeId * NodeSize)
            long offset = HeaderSize + (long)nodeId * Header.PageSize;
            return offset >= 0 && offset < MyFileStream.Length;
        }

        /// <summary>
        /// Recursively traverses the B+ Tree to identify all reachable nodes, populating a set of active node IDs.
        /// </summary>
        /// <remarks>
        /// This acts as a "mark" phase for compaction. It performs deep validation on node offsets 
        /// and handles cycle detection (via BitArray) to ensure only structurally sound, 
        /// accessible data is preserved in the storage file.
        /// </remarks>
        private void FindLiveNodes(int nodeId, BitArray liveNodes)
        {
            // 1. Boundary Check: Prevent EndOfStreamException
            if (nodeId < 0 || !IsValidNodeOffset(nodeId) || nodeId > liveNodes.Count)
                return;

            // 2. Have we already been here before?
            if (liveNodes.Get(nodeId))
            {
                throw new ArgumentException(nameof(nodeId), "Cycle Detected");
            }

            // 3. Mark the node.
            liveNodes.Set(nodeId, true);
            BNode node = DiskRead(nodeId);

            if (!node.Leaf)
            {
                // 4. Visit each child.
                for (int i = 0; i <= node.NumKeys; i++)
                {
                    int childId = node.GetChildId(i);
                    if (childId != -1)
                    {
                        FindLiveNodes(childId, liveNodes);
                    }
                }
            }
        }

        /// <summary>
        /// Returns a list of node IDs that are leaked (orphaned) within the physical file.
        /// </summary>
        /// <remarks>
        /// Performs an audit by marking all reachable nodes and all nodes in the FreeList 
        /// into a bitmask. Any bit remaining unset represents a 'Zombie'—a node that is 
        /// neither part of the tree nor available for reuse, indicating a leak or corruption.
        /// </remarks>
        public void GetZombies(List<int> zombies)
        {
            if (Header.RootId == -1) return;
            if (Header.NodeCount == 0) return;

            // 1. Mark all reachable nodes.
            BitArray accountedFor = new BitArray(Header.NodeCount);
            FindLiveNodes(Header.RootId, accountedFor);

            // 2. Mark all free nodes in the same BitArray.
            foreach (int id in FreeList)
            {
                accountedFor.Set(id, true);
            }

            // 3. Any index still false is a zombie.
            for (int i = 0; i < Header.NodeCount; i++)
            {
                if (!accountedFor.Get(i))
                {
                    if (!zombies.Contains(i))
                        zombies.Add(i);
                }
            }
        }

        /// <summary>
        /// Returns the total count of leaked (orphaned) nodes.
        /// </summary>
        /// <remarks>
        /// Useful for health checks and determining if a Compact operation is necessary. 
        /// Utilizes the same bitmask audit logic as GetZombies.
        /// </remarks>
        public int CountZombies()
        {
            if (Header.RootId == -1) return 0;
            if (Header.NodeCount == 0) return 0;

            // 1. Mark all reachable nodes.
            BitArray accountedFor = new BitArray(Header.NodeCount);
            FindLiveNodes(Header.RootId, accountedFor);

            // 2. Mark all free nodes in the same BitArray.
            foreach (int id in FreeList)
            {
                accountedFor.Set(id, true);
            }

            int count = 0;

            // 3. All nodes that are false are zombie nodes.
            for (int i = 0; i < Header.NodeCount; i++)
            {
                if (!accountedFor.Get(i))
                {
                    count++;
                }
            }

            return count;
        }


        /// ------- PRINT METHODS ---------

        /// <summary>
        /// Counts the total number of keys stored across the B+ Tree by starting at the
        /// leftmost leaf and summing NumKeys across the linked leaf chain.
        /// </summary>
        /// <returns>The total number of keys in the tree.</returns>
        public int CountKeys()
        {
            if (Header.RootId == -1) return 0;
            BNode rootNode = DiskRead(Header.RootId);
            if (rootNode.NumKeys == 0) return 0;

            int count = 0;
            BNode current = FindLeftMostLeaf(Header.RootId);
            while (current != null)
            {
                count += current.NumKeys;
                if (current.NextLeafId == -1) break;
                current = DiskRead(current.NextLeafId);
            }
            return count;
        }

        /// <summary>
        /// Collects all keys in sorted order by traversing the linked leaf chain
        /// starting from the leftmost leaf node via NextLeafId.
        /// </summary>
        /// <returns>A list of all keys in ascending order.</returns>
        public List<int> GetKeys()
        {
            if (Header.RootId == -1) return new List<int>();
            BNode rootNode = DiskRead(Header.RootId);
            if (rootNode.NumKeys == 0) return new List<int>();

            List<int> list = new List<int>();
            BNode current = FindLeftMostLeaf(Header.RootId);
            while (current != null)
            {
                var keys = current.GetAllElements().Take(current.NumKeys).Select(e => e.Key);
                list.AddRange(keys);

                if (current.NextLeafId == -1) break;
                current = DiskRead(current.NextLeafId);
            }
            return list;
        }

        /// <summary>
        /// Returns a flat list of all elements stored in the B+ Tree in sorted order.
        /// This method leverages the B+ Tree's linked leaf structure: it finds the 
        /// leftmost leaf and then performs a linear scan across the sibling chain 
        /// (NextLeafId) to gather all data records without needing to revisit internal index nodes.
        /// </summary>
        public List<Element> GetElements()
        {
            var list = new List<Element>();
            if (Header.RootId == -1) return list;
            BNode rootNode = DiskRead(Header.RootId);
            if (rootNode.NumKeys == 0) return list;

            BNode current = FindLeftMostLeaf(Header.RootId);
            while (current != null)
            {
                var keys = current.GetAllElements().Take(current.NumKeys);
                list.AddRange(keys);

                if (current.NextLeafId == -1) break;
                current = DiskRead(current.NextLeafId);
            }
            return list;
        }


        /// <summary>
        /// Extracts all tree data into a flat CSV format, acting as a Recovery Dump.
        /// </summary>
        /// <remarks>
        /// Designed to facilitate a "Dump and Reload" strategy for repairing corrupted or 
        /// severely unbalanced trees. The output file preserves Key/Data pairs in sorted order, 
        /// providing a clean source for BulkLoad operations to rebuild a healthy structure.
        /// </remarks>
        /// 
        public void WriteToFile(string fileName)
        {
            File.Delete(fileName);
            if (Header.RootId == -1) return;

            BNode rootNode = DiskRead(Header.RootId);
            if (rootNode.NumKeys == 0) return;

            using (StreamWriter sw = new StreamWriter(fileName, false, Encoding.UTF8, 65536)) // 64KB buffer
            {
                BNode? current = FindLeftMostLeaf(Header.RootId);
                while (current != null)
                {
                    // Use a standard for-loop to avoid LINQ/Iterator allocations
                    int count = current.NumKeys;
                    for (int i = 0; i < count; i++)
                    {
                        // Direct access is faster than fetching the whole Element array
                        sw.Write(current.GetKey(i));
                        sw.Write(", ");
                        sw.WriteLine(current.GetData(i));
                    }

                    if (current.NextLeafId == -1) break;
                    current = DiskRead(current.NextLeafId);
                }
            }
        }

        /// <summary>
        /// Performs a level-order (Breadth-First Search) traversal of the B+ Tree to visualize its 
        /// hierarchical structure. This method treats the tree as a series of levels, starting 
        /// from the root index down to the linked leaf nodes.
        /// It uses a queue with a null marker to distinguish between different levels of the tree
        /// during the print process.
        /// </summary>
        public void PrintTreeByLevel()
        {
            if (Header.RootId == -1) return;

            BNode rootNode = DiskRead(Header.RootId);

            if (rootNode.NumKeys == 0)
            {
                Console.WriteLine("The Tree is empty.");
            }

            Queue<BNode> queue = new Queue<BNode>();

            // Using a null marker to distinguish levels.
            BNode marker = null;

            rootNode = DiskRead(Header.RootId); // Start with the latest root.
            queue.Enqueue(rootNode);
            queue.Enqueue(marker); // Initial level marker.

            while (queue.Count > 0)
            {
                BNode current = queue.Dequeue();

                if (current == marker)
                {
                    Console.WriteLine();
                    if (queue.Count > 0)
                    {
                        queue.Enqueue(marker); // Add marker for the next level
                    }
                    continue;
                }

                // Print the keys of the current node
                PrintNodeKeys(current);

                // Iterate through potential keys, reading non-negative key values as child node IDs.
                if (current.Leaf == false)
                {
                    for (int i = 0; i <= current.NumKeys; i++)
                    {
                        int childId = current.GetChildId(i);
                        if (childId != -1)
                        {
                            BNode child = DiskRead(childId);
                            queue.Enqueue(child);
                        }
                    }
                }
            }
            Console.WriteLine();
        }

        /// <summary>
        /// Prints the node's active keys to the console without extra memory allocations.
        /// </summary>
        private static void PrintNodeKeys(BNode node)
        {
            List<string> activeKeys = new List<string>();
            int count = node.NumKeys;
            for (int i = 0; i < count; i++)
            {
                string key = node.GetKey(i).ToString();
                activeKeys.Add(key);
            }
            Console.WriteLine($"[{string.Join(", ", activeKeys)}]");
        }

        /// <summary>
        /// Performs a breadth-first (level-order) traversal of the tree.
        /// Visualizes the tree structure layer-by-layer, which is ideal for 
        /// verifying balance and node distribution.
        /// </summary>
        public void PrintTreeSimple(int rootPageId)
        {
            if (rootPageId == -1)
            {
                Console.WriteLine("\nTree is empty.");
                return;
            }

            var queue = new Queue<(int pageId, int level)>();
            queue.Enqueue((rootPageId, 0));
            int currentLevel = -1;

            while (queue.Count > 0)
            {
                var (pageId, level) = queue.Dequeue();

                if (level > currentLevel)
                {
                    Console.WriteLine($"\n--- Level {level} ---");
                    currentLevel = level;
                }

                BNode node = DiskRead(pageId);

                // Indicate if this is an internal node or a data leaf
                string typeLabel = node.Leaf ? "[LEAF]" : "[INT]";
                Console.Write($"{typeLabel} P{pageId}: ");

                PrintNodeKeys(node);

                // B+ Tree Specific: Show the horizontal link between leaves
                if (node.Leaf)
                {
                    string nextLink = node.NextLeafId == -1 ? "null" : $"P{node.NextLeafId}";
                    Console.Write($" -> Next: {nextLink}");
                }

                Console.Write(" | ");

                // Enqueue children for the next level
                if (!node.Leaf)
                {
                    for (int i = 0; i <= node.NumKeys; i++)
                    {
                        if (node.GetChildId(i) != -1)
                            queue.Enqueue((node.GetChildId(i), level + 1));
                    }
                }

                // Add a newline if the next node is on a different level to keep it readable
                if (queue.Count > 0 && queue.Peek().level > level)
                {
                    Console.WriteLine();
                }
            }
            Console.WriteLine();
        }

        /// <summary>
        /// Demonstrates the sequential access power of the B+ Tree by traversing 
        /// the "Leaf Chain." It starts at the first logical record and follows 
        /// the NextLeafId pointers to visit every leaf node in sorted order.
        /// This bypasses the index entirely, showing how B+ Trees optimize 
        /// for table scans and sequential reporting.
        /// </summary>
        /// 
        public void PrintLeafChain()
        {
            Console.WriteLine("\n--- Leaf Chain Scan ---");
            if (Header.RootId == -1) return;
            BNode current = FindLeftMostLeaf(Header.RootId);

            while (current != null)
            {
                Console.Write($"P{current.Id}");
                PrintNodeKeys(current);
                Console.Write(" -> ");

                // Move to next leaf.
                if (current.NextLeafId == -1) break;
                current = DiskRead(current.NextLeafId);
            }
            Console.WriteLine("NULL");
        }

        /// <summary>
        /// Recursively descends the leftmost branch of the B+ Tree to find the starting leaf node.
        /// This is the standard entry point for linear scans, range queries, and min-key 
        /// operations. In a B+ Tree, following the first child pointer (index 0) at every 
        /// internal level is guaranteed to lead to the smallest element in the entire structure.
        /// </summary>
        private BNode FindLeftMostLeaf(int pageId)
        {
            BNode node = DiskRead(pageId);
            while (!node.Leaf)
            {
                // Always follow the VERY FIRST child (index 0)
                node = DiskRead(node.GetChildId(0));
            }
            return node;
        }

        /// <summary>
        /// Outputs a physical representation of the underlying storage, 
        /// iterating through every allocated page index to display node keys and metadata.
        /// </summary>
        public void DumpFile()
        {
            Console.WriteLine("--- PHYSICAL DISK DUMP ---");
            Console.WriteLine($"RootId: {Header.RootId}, Total Nodes: {Header.NodeCount}");

            for (int i = 0; i < Header.NodeCount; i++)
            {
                if (FreeList.Contains(i))
                {
                    Console.WriteLine($"Page {i}: [FREE]");
                    continue;
                }

                try
                {
                    BNode node = DiskRead(i);
                    Console.Write($"Page {i}: ");
                    PrintNodeKeys(node);
                    Console.WriteLine($"(Leaf: {node.Leaf})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Page {i}: [CORRUPT {ex.Message}]");
                }
            }
        }


        /// <summary>
        /// Performs a comprehensive diagnostic scan of the B+ Tree's internal structural pointers.
        /// This method iterates through every page in the file to identify internal index nodes, 
        /// reporting their key counts and the specific child page IDs (pointers) they reference. 
        /// It serves as a vital integrity check to ensure that the hierarchical relationship 
        /// between the root, intermediate index levels, and the terminal leaf pages remains 
        /// consistent and free of corruption.
        /// </summary>
        public void PrintPointers()
        {
            Console.WriteLine("--- POINTER INTEGRITY CHECK ---");
            Console.WriteLine($"RootId: {Header.RootId}");
            for (int i = 0; i < Header.NodeCount; i++)
            {
                try
                {
                    BNode node = DiskRead(i);
                    if (node.Leaf) continue;

                    Console.Write($"Internal Node {i} [Keys: {node.NumKeys}]: ");
                    for (int j = 0; j <= node.NumKeys; j++)
                    {
                        Console.Write($"Kid[{j}]->Page {node.GetChildId(j)} | ");
                    }
                    Console.WriteLine();
                }
                catch { }
            }
        }

        /// <summary>
        /// Print the Tree.  
        /// </summary>
        public void PrintByRoot()
        {
            Console.WriteLine("--- PRINT BY ROOT ---");
            Console.WriteLine($"RootId: {Header.RootId}");
            PrintByRootRecursive(Header.RootId);
        }

        /// <summary>
        /// Recursively prints the B+ Tree structure starting from a specific node, 
        /// using indentation to visualize the hierarchy levels.
        /// </summary>
        private void PrintByRootRecursive(int nodeId, int level = 0)
        {
            if (nodeId == -1) return;
            BNode node = DiskRead(nodeId);
            string indent = new string(' ', level * 4);

            // 1. Print current node header and keys
            Console.Write($"{indent}NODE {node.Id}: ");
            PrintNodeKeys(node);
            Console.WriteLine();

            // 2. Recursively print children with increased indentation
            if (!node.Leaf)
            {
                for (int i = 0; i <= node.NumKeys; i++)
                {
                    Console.WriteLine($"{indent}  Child {i} (ID: {node.GetChildId(i)})");
                    PrintByRootRecursive(node.GetChildId(i), level + 1);
                }
            }
        }

        // ----- GHOST NODES --------

        /// <summary>
        /// Validates the tree's structural integrity by ensuring no internal nodes are empty.
        /// Empty non-root nodes ("Ghost Nodes") indicate a corruption in the balancing logic.
        /// </summary>
        public void CheckGhost()
        {
            if (Header.NodeCount == 0) return;
            BitArray visited = new BitArray(Header.NodeCount);
            CheckGhostRecursive(Header.RootId, visited);
        }

        /// <summary>
        /// Recursively traverses the tree to verify that every reachable node contains data.
        /// Also performs cycle detection and bounds checking on key counts.
        /// </summary>
        private void CheckGhostRecursive(int nodeId, BitArray visited = null)
        {
            if (nodeId <= 0) return;
            if (Header.NodeCount == 0) return;

            if (visited == null)
            {
                visited = new BitArray(Header.NodeCount);
            }

            // Check for cycles.
            if (visited.Get(nodeId))
            {
                throw new ArgumentException(nameof(nodeId), "Cycle Detected");
            }

            BNode node = DiskRead(nodeId);
            visited.Set(nodeId, true);
            if (nodeId != Header.RootId && node.NumKeys == 0)
            {
                throw new ArgumentException(nameof(nodeId), "Ghost Detected");
            }

            if (!node.Leaf)
            {
                if (node.NumKeys > Header.Order)
                {
                    throw new ArgumentOutOfRangeException(nameof(node.NumKeys));
                }

                for (int i = 0; i <= node.NumKeys; i++)
                {
                    if (node.GetChildId(i) != -1)
                    {
                        CheckGhostRecursive(node.GetChildId(i), visited);
                    }
                }
            }
        }

        /// <summary>
        /// Scans the entire physical file to count "Ghost" nodes (nodes with zero keys).
        /// </summary>
        /// <remarks>
        /// Unlike CountZombies, this is an I/O-intensive brute-force scan that inspects 
        /// every node's content. It identifies nodes that are physically present but logically empty; 
        /// these may be valid empty nodes, newly allocated space, or remnants of a failed deletion.
        /// </remarks>
        public int CountGhost()
        {
            int count = 0;
            for (int i = 0; i < Header.NodeCount; i++)
            {
                try
                {
                    BNode node = DiskRead(i);
                    if (node.NumKeys == 0)
                    {
                        count++;
                    }
                }
                catch { }
            }
            return count;
        }

        // -------- VALIDATION METHODS ---------

        /// <summary>
        /// Validate structural integrity: checks for cycles, ordering, boundary constraints and minimum keys.
        /// Does NOT validate free list correctness or external file-format corruption beyond Header.Magic.
        /// </summary>
        public void ValidateIntegrity()
        {
            if (Header.RootId == -1) return;
            if (Header.NodeCount == 0) return;

            BitArray visited = new BitArray(Header.NodeCount);
            CheckNodeIntegrity(Header.RootId, int.MinValue, int.MaxValue, visited);

            // Optional: Check if NodeCount matches what is actually on disk
            if (visited.Count > Header.NodeCount)
                throw new Exception(message: "Reachable nodes cannot exceed NodeCount.");
        }

        /// <summary>
        /// Performs a deep structural audit of a node and its descendants. 
        /// Validates key ordering, underflow conditions, and enforces that all 
        /// child keys fall strictly within the logical boundaries defined by their parent keys.
        /// </summary>
        private void CheckNodeIntegrity(int nodeId, int min, int max, BitArray visited)
        {
            if (nodeId == -1) return;
            if (nodeId > visited.Count) return;

            if (visited.Get(nodeId))
            {
                throw new ArgumentException(nameof(nodeId), $"Cycle Detected at node {nodeId}");
            }

            visited.Set(nodeId, true);
            BNode node = DiskRead(nodeId);

            // 1. Verify Minimum Keys (except the Root).
            int effectiveOrder = Header.Order;
            int t = (effectiveOrder + 1) / 2;
            bool isRoot = (nodeId == Header.RootId);

            // Only enforce minimum keys if it's NOT the root node.
            if (!isRoot)
            {
                if (node.Leaf)
                {
                    // Variable-length leaves split on physical byte capacity, so they can have
                    // fewer than t - 1 keys, but should not be empty after normal operations.
                    if (node.NumKeys == 0)
                    {
                        throw new ArgumentException("EmptyLeaf", nameof(nodeId));
                    }
                }
                else if (node.NumKeys < t - 1)
                {
                    throw new ArgumentException("Underflow", nameof(nodeId));
                }
            }

            for (int i = 0; i < node.NumKeys; i++)
            {
                // 1. Get current Key.
                int currentKey = node.GetKey(i);

                // 2. Verify Key Ordering within node.
                if (i > 0 && currentKey <= node.GetKey(i - 1))
                    throw new ArgumentException($"Key {currentKey} must be sorted.");

                // 3. Verify Key is within Parent's Range.
                if (currentKey < min || currentKey > max)
                {
                    throw new ArgumentOutOfRangeException(nameof(currentKey));
                }

                // 4. Recurse into children with updated boundaries.
                if (!node.Leaf)
                {
                    int leftChildMin = (i == 0) ? min : node.GetKey(i - 1);
                    CheckNodeIntegrity(node.GetChildId(i), leftChildMin, currentKey, visited);

                    // If it's the last key, also check the rightmost child
                    if (i == node.NumKeys - 1)
                    {
                        CheckNodeIntegrity(node.GetChildId(i + 1), currentKey, max, visited);
                    }
                }
            }
        }

        // -------- DIAGNOSTIC METHODS ---------

        /// <summary>
        /// Calculates the current height of the B+ Tree by traversing the leftmost path 
        /// from the root to a leaf node. 
        /// </summary>
        public int GetHeight()
        {
            int height = 0;
            int currentId = this.Header.RootId;

            while (currentId != -1)
            {
                height++;
                var node = this.DiskRead(currentId);
                if (node.Leaf) break;
                currentId = node.GetChildId(0);
            }
            return height;
        }

        // -------- AUDIT METHODS ---------

        /// <summary>
        /// A data transfer object containing a snapshot of the B+ Tree's physical and logical health.
        /// </summary>
        public class TreeHealthReport
        {
            public int Height { get; set; }
            public int ZombieCount { get; set; }
            public int GhostCount { get; set; }
            public int ReachableNodes { get; set; }
            public int TotalKeys { get; set; }
            public double AverageDensity { get; set; }
            public int LeafChainCount { get; set; }
        }

        /// <summary>
        /// Executes a comprehensive dual-phase audit of the B+ Tree to evaluate storage efficiency, 
        /// structural integrity, and leaf-level connectivity.
        /// </summary>
        /// <returns>A TreeHealthReport detailing fragmentation, orphan nodes, and tree geometry.</returns>
        /// <remarks>
        /// This method validates the B+ Tree from two perspectives:
        /// 1. Vertical Consistency: A recursive traversal from the root ensures all index levels 
        ///    correctly point to their descendants and verifies total height.
        /// 2. Horizontal Consistency: A scan of the Leaf Chain verifies that the sibling pointers 
        ///    (NextLeafId) accurately represent the sorted dataset independently of the index.
        /// It identifies "Zombies" (unreferenced pages not in the FreeList) and calculates 
        /// the utilization density of the reachable index and data nodes.
        /// </remarks>
        public TreeHealthReport PerformFullAudit()
        {
            var report = new TreeHealthReport();
            if (Header.RootId == -1 || Header.NodeCount == 0)
            {
                return report;
            }

            BitArray accountedFor = new BitArray(Header.NodeCount);

            // 1. Vertical Recursive Scan (Finds ReachableNodes, TotalKeys, and Height)
            report.Height = AuditRecursive(Header.RootId, 1, accountedFor, report);

            // 2. Horizontal Leaf Chain Scan
            report.LeafChainCount = GetLeafChainCount();

            // 3. Average Density Calculation
            if (report.ReachableNodes > 0)
            {
                double totalCapacity = (double)report.ReachableNodes * (Header.Order - 1);
                report.AverageDensity = (report.TotalKeys / totalCapacity) * 100.0;
            }

            // 4. Count Zombies (Nodes in file that were never reached).
            foreach (int id in FreeList)
            {
                if (id >= 0 && id < Header.NodeCount) accountedFor.Set(id, true);
            }

            for (int i = 0; i < Header.NodeCount; i++)
            {
                if (!accountedFor.Get(i)) report.ZombieCount++;
            }

            return report;
        }


        /// <summary>
        /// The recursive engine for PerformFullAudit. Traverses the tree to discover 
        /// live nodes and calculate height.
        /// </summary>
        /// <returns>The maximum depth reached by this subtree.</returns>
        private int AuditRecursive(int id, int currentDepth, BitArray accountedFor, TreeHealthReport report)
        {
            // Ghost Check: Pointer points outside the physical file.
            if (id < 0 || id >= Header.NodeCount)
            {
                report.GhostCount++;
                return currentDepth;
            }

            if (id > accountedFor.Count) return 0;

            // Circular Reference Check: Prevents infinite recursion.
            if (accountedFor.Get(id)) return currentDepth;

            accountedFor.Set(id, true);
            report.ReachableNodes++;

            var node = DiskRead(id);

            if (node.Leaf)
            {
                report.TotalKeys += node.NumKeys;
                return currentDepth;
            }

            int maxSubtreeHeight = currentDepth;
            for (int i = 0; i <= node.NumKeys; i++)
            {
                int childHeight = AuditRecursive(node.GetChildId(i), currentDepth + 1, accountedFor, report);
                if (childHeight > maxSubtreeHeight) maxSubtreeHeight = childHeight;
            }

            return maxSubtreeHeight;
        }

        /// <summary>
        /// Performs a linear tally of all data records by traversing the B+ Tree's leaf-level linked list.
        /// This method provides an absolute count of stored elements by bypassing the internal index
        /// and visiting each leaf node sequentially via the sibling pointers. It is an essential
        /// diagnostic tool for verifying that the total record count matches the index's expectations.
        /// </summary>
        public int GetLeafChainCount()
        {
            int count = 0;
            BNode current = FindLeftMostLeaf(Header.RootId);
            while (current != null)
            {
                count += current.NumKeys;
                if (current.NextLeafId == -1) break;
                current = DiskRead(current.NextLeafId);
            }
            return count;
        }

        /// <summary>
        /// Orchestrates a full structural audit and outputs the results to the console.
        /// This provides a snapshot of the B+ Tree's physical health, contrasting 
        /// vertical index metrics against the horizontal leaf chain to identify 
        /// structural anomalies, wasted space, or pointer corruption.  
        /// 'Zombies' are orphaned pages not reachable from the root.
        /// 'Ghosts' are dangling pointers to unallocated file space.
        /// </summary>
        public void PrintAuditReport()
        {
            var report = PerformFullAudit();
            Console.WriteLine("--- B+ Tree Health Report ---");
            Console.WriteLine($"Height: {report.Height}");
            Console.WriteLine($"Reachable Nodes: {report.ReachableNodes}");
            Console.WriteLine($"Total Keys: {report.TotalKeys}");
            Console.WriteLine($"Average Node Density: {report.AverageDensity:F2}%");
            Console.WriteLine($"Zombies (Unreachable Nodes): {report.ZombieCount}");
            Console.WriteLine($"Ghosts (Dangling Pointers): {report.GhostCount}");
            Console.WriteLine($"Horizontal Count: {report.LeafChainCount}");
        }

        /// <summary>
        /// Diagnostic helper to trace a key's path and inspect the target node's internal state.
        /// </summary>
        public void DiagnoseKeyLocation(int targetKey)
        {
            Console.WriteLine($"\n--- DIAGNOSTIC TRACE FOR KEY: {targetKey} ---");
            if (Header.RootId == -1)
            {
                Console.WriteLine("Tree is empty.");
                return;
            }

            int currentId = Header.RootId;
            while (true)
            {
                BNode node = DiskRead(currentId);
                string typeLabel = node.Leaf ? "[LEAF]" : "[INT]";

                // Extract keys safely
                var keys = new List<int>();
                for (int i = 0; i < node.NumKeys; i++)
                {
                    keys.Add(node.GetKey(i));
                }

                Console.WriteLine($"{typeLabel} Node ID: {node.Id}, NumKeys: {node.NumKeys}, Keys: [{string.Join(", ", keys)}]");

                // Check internal sorting invariant
                for (int i = 1; i < keys.Count; i++)
                {
                    if (keys[i] <= keys[i - 1])
                    {
                        Console.WriteLine($" -> [WARNING] Sorting violation detected in Node {node.Id}: Key {keys[i]} <= {keys[i - 1]}");
                    }
                }

                if (node.Leaf)
                {
                    // Search within the leaf
                    int index = node.FindIndex(targetKey);
                    if (index >= 0)
                    {
                        Console.WriteLine($" -> SUCCESS: Key {targetKey} found at leaf node {node.Id}, index {index}. Data: {node.GetData(index)}");
                    }
                    else
                    {
                        Console.WriteLine($" -> NOT FOUND: Key {targetKey} is missing from leaf node {node.Id} (Insertion point would be: {~index})");
                    }
                    break;
                }
                else
                {
                    // Descend to child branch
                    int branchIdx = node.FindChildBranchIndex(targetKey);
                    int nextId = node.GetChildId(branchIdx);
                    Console.WriteLine($" -> Descending via branch index {branchIdx} to Child ID: {nextId}");

                    if (nextId == -1)
                    {
                        Console.WriteLine($" -> [FATAL] Encountered a -1 child pointer at branch index {branchIdx}!");
                        break;
                    }
                    currentId = nextId;
                }
            }
            Console.WriteLine("--------------------------------------------\n");
        }

        /// <summary>
        /// Diagnostic helper to print nodes from disk.
        /// </summary>
        public void ShowBlocks(List<int> blockIds)
        {
            foreach (int id in blockIds)
            {
                Console.WriteLine($"P{id}: {DiskRead(id)}");
            }
        }

        /// <summary>
        /// Diagnostic helper to print node details.
        /// </summary>
        public void PrintNodeDetails(List<int> blockIds)
        {
            foreach (int id in blockIds)
            {
                BNode node = DiskRead(id);

                var keys = new List<int>(node.NumKeys);
                for (int i = 0; i < node.NumKeys; i++)
                {
                    keys.Add(node.GetKey(i));
                }

                string detail = node.Leaf
                    ? $"NextLeaf={node.NextLeafId}"
                    : $"Children=[{string.Join(", ", Enumerable.Range(0, node.NumKeys + 1).Select(i => node.GetChildId(i)))}]";

                Console.WriteLine($"P{id}: Node {node.Id} (Leaf={node.Leaf}, NumKeys={node.NumKeys}) " +
                    $"Keys=[{string.Join(", ", keys)}] {detail}");
            }
        }


        /// <summary>
        /// Diagnostic helper that prints the root-to-leaf traversal path for a given key,
        /// logging each visited node's metadata, keys, child pointers (or next-leaf link),
        /// and chosen branch index to help trace routing and rebalance bugs.
        /// </summary>
        public void TracePath(int targetKey)
        {
            if (Header.RootId == -1)
            {
                Console.WriteLine($"=== PATH TO {targetKey}: Tree is empty ===");
                return;
            }

            int currentId = Header.RootId;
            int? parentId = null;
            Console.WriteLine($"=== PATH TO {targetKey} ===");

            while (true)
            {
                BNode node = DiskRead(currentId);

                var keys = new List<int>(node.NumKeys);
                for (int i = 0; i < node.NumKeys; i++)
                {
                    keys.Add(node.GetKey(i));
                }

                string parentLabel = parentId.HasValue ? parentId.Value.ToString() : "NONE";
                string detail = node.Leaf
                    ? $"NextLeaf={node.NextLeafId}"
                    : $"Children=[{string.Join(", ", Enumerable.Range(0, node.NumKeys + 1).Select(i => node.GetChildId(i)))}]";

                Console.WriteLine(
                    $"  Node {node.Id} (Leaf={node.Leaf}, Parent={parentLabel}, NumKeys={node.NumKeys}) " +
                    $"Keys=[{string.Join(", ", keys)}] {detail}");

                if (node.Leaf) break;

                int childIndex = node.FindChildBranchIndex(targetKey);
                Console.WriteLine($"    -> BranchIndex={childIndex} (key {targetKey})");

                parentId = node.Id;
                currentId = node.GetChildId(childIndex);
            }
        }


    }
}
