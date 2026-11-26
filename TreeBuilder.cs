namespace SparrowTwo
{
    /// <summary>
    /// Implements a top-down bulk-loading algorithm to create a balanced B+ Tree 
    /// from a sorted list of elements. This is significantly faster than inserting keys one-by-one.
    /// </summary>
    public class TreeBuilder
    {
        private int PageSize;
        private int Order;
        private int LeafTarget;   // Target number of keys per leaf based on fill factor.
        private double LeafFill;

        private TreeManager Manager;
        private BNode LastLeaf = null;

        private const int MINPAGESIZE = BTree.MINPAGESIZE;
        private const int MINORDER = BTree.MINORDER;
        private const int LeafHeaderAndSlotOverhead = 16; 
        private const int SlotEntryBytes = 8;           


        // --- WRITE BUFFER ---
        private Dictionary<int, BNode> Buffer = new Dictionary<int, BNode>();
        private const int MaxBufferSize = 32;

        /// <summary>
        /// Initializes the builder with specific fill constraints.
        /// </summary>
        public TreeBuilder(int pageSize = 4096, double leafFill = 0.8)
        {
            PageSize = pageSize;
            Order = BNode.CalculateMaxOrder(pageSize);
            LeafFill = Math.Clamp(leafFill, 0.5, 1.0);

            // Calculate how many keys we aim to put in each leaf to allow for future growth
            int target = (int)((Order - 1) * LeafFill);
            LeafTarget = Math.Clamp(target, 1, Order - 1);

            if (PageSize < MINPAGESIZE)
                throw new ArgumentException($"Page Size must be at least {MINPAGESIZE} bytes.");

            if (Order < MINORDER)  
                throw new ArgumentException($"Order must be at least {MINORDER}.");
        }

        /// <summary>
        /// Stages a modified node in the memory buffer before it is committed to physical storage.
        /// This method implements "Write Coalescing" by using the node ID as a unique key; 
        /// multiple updates to the same node in a single transaction will only result 
        /// in a single disk write. When the buffer reaches its capacity threshold, 
        /// a flush is automatically triggered to prevent memory exhaustion.
        /// </summary>
        private void BufferedSave(BNode node)
        {
            // Use a Dictionary to ensure we only hold one copy of a node by its ID
            Buffer[node.Id] = node;

            if (Buffer.Count >= MaxBufferSize)
            {
                FlushBuffer();
            }
        }

        /// <summary>
        /// Commits all staged nodes from the memory buffer to the physical disk.
        /// This batch-writing process minimizes disk head movement and system call overhead.
        /// Once the Disk Manager confirms all nodes are persisted, the buffer is cleared 
        /// to prepare for the next set of operations.
        /// </summary>
        private void FlushBuffer()
        {
            foreach (var node in Buffer.Values)
            {
                Manager.SaveToDisk(node);
            }
            Buffer.Clear();
        }

        /// <summary>
        /// The entry point for building a tree. It validates the input, 
        /// manages the lifecycle of the TreeManager, and saves the final Root ID.
        /// </summary>
        public void CreateFromSorted(string path, List<Element> keys)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is empty.");
            if (keys == null || keys.Count == 0) return;
            if (!Util.IsSortedList(keys)) throw new ArgumentException("The keys must be sorted.");

            using (Manager = new TreeManager(path, PageSize, Order))
            {
                BNode rootNode = Build(keys, 0, keys.Count - 1);

                // Final Flush ensures last leaf and root hit the disk
                FlushBuffer();

                if (rootNode != null)
                {
                    Manager.Header.RootId = rootNode.Id;
                    Manager.SaveHeader();
                }
            }
        }


        /// <summary>
        /// Recursively constructs a subtree from a sorted range of elements using a bottom-up 
        /// bulk-loading approach. If the range fits within the target density, a terminal 
        /// leaf node is created and integrated into the horizontal Leaf Chain. Otherwise, 
        /// an internal index node is generated, and the range is partitioned to build the 
        /// child branches recursively.
        /// </summary>
        private BNode Build(List<Element> keys, int start, int end)
        {
            int count = end - start + 1;
            if (count <= 0) return null;

            // Pack into a single leaf only if all items in [start..end] fit within
            // Order and the physical byte budget for a leaf page.
            if (count <= Order - 1 && FitsInLeafBudget(keys, start, end))
            {
                BNode leaf = Manager.CreateNode(true);
                leaf.NextLeafId = -1;

                for (int i = 0; i < count; i++)
                {
                    leaf.InsertAt(i, keys[start + i].Key, keys[start + i].Data);
                }

                LinkLeaf(leaf);
                BufferedSave(leaf);
                return leaf;
            }

            // Otherwise, build leaf runs bounded by physical byte capacity,
            // then group them under internal index nodes.
            var childSubtrees = new List<(int FirstKey, BNode Node)>();
            int cursor = start;

            while (cursor <= end)
            {
                int runEnd = FindLeafRunEnd(keys, cursor, end);
                BNode child = Build(keys, cursor, runEnd);
                childSubtrees.Add((keys[cursor].Key, child));
                cursor = runEnd + 1;
            }

            return BuildInternalLevels(childSubtrees);
        }


        // --- Node Helpers ---


        /// <summary>
        /// A lightweight, stack-allocated structure used to define a contiguous range 
        /// within an array or list. This avoids the overhead of object allocation 
        /// and ensures compatibility across .NET versions that do not support 
        /// the native System.Index or System.Range types.
        /// </summary>
        public struct SimpleRange
        {
            public int Start;
            public int End;
            public SimpleRange(int s, int e) { Start = s; End = e; }
        }

        /// <summary>
        /// Divide and conquer: Partitions a large range of elements into optimal sub-ranges 
        /// for child node construction. This logic determines the branching "width" of 
        /// the current internal node by calculating how many children are required to 
        /// support the total element count at the current tree height, ensuring 
        /// balanced data distribution across all branches.
        /// </summary>
        private List<SimpleRange> PartitionRange(int start, int end, int order, int height)
        {
            int totalElements = end - start + 1;
            List<SimpleRange> partitions = new List<SimpleRange>();

            long childMax = CalculateMaxCapacity(height - 1, order, LeafTarget);
            int numChildren = (int)Math.Ceiling((double)totalElements / childMax);

            int currentStart = start;
            for (int i = 0; i < numChildren; i++)
            {
                int remainingElements = end - currentStart + 1;
                int remainingChildren = numChildren - i;
                int stride = (int)Math.Ceiling((double)remainingElements / remainingChildren);

                int currentEnd = currentStart + stride - 1;
                partitions.Add(new SimpleRange(currentStart, currentEnd));

                currentStart = currentEnd + 1;
            }

            return partitions;
        }

        /// <summary>
        /// Calculates the theoretical maximum number of elements a subtree can hold 
        /// given its height and the branching order. This geometric calculation 
        /// is the foundation of the bulk-loading "blueprint," allowing the builder 
        /// to pre-partition data into perfectly sized branches.
        /// </summary>
        private long CalculateMaxCapacity(int height, int order, int leafTarget)
        {
            if (height <= 0) return 0;
            if (height == 1) return leafTarget;

            // A tree with height 'h' can hold: (order ^ (h-1)) * leafTarget.
            double capacity = Math.Pow(order, height - 1) * leafTarget;
            if (capacity >= long.MaxValue)  return long.MaxValue;
            return (long)capacity;
        }


        /// <summary>
        /// Determines the necessary B+ Tree height to accommodate the given number of elements
        /// while respecting the LeafFill/LeafTarget constraints.
        /// </summary>
        private int CalculateHeight(int totalElements, int order)
        {
            if (totalElements <= 0) return 0;

            // If it fits in a single leaf (using the Order limit), height is 1
            if (totalElements <= order - 1) return 1;

            int height = 1;
            long capacity = LeafTarget; // The base level (leaves) can hold this many

            // Keep adding internal levels (multiplied by order) until we can fit everything
            while (totalElements > capacity)
            {
                height++;
                // check for overflow.
                if (capacity > long.MaxValue / order)
                    break;

                capacity *= order;

                // Logical ceiling for safety.
                if (height > 10) break; // Safety against infinite loops.
            }
            return height;
        }

        /// <summary>
        /// Calculates the starting byte offset of the data heap after the header, child pointer table, and slot table.
        /// </summary>
        private int GetDataAreaStart()
        {
            int kidTableBytes = (Order + 1) * 4; // KID_SIZE = 4
            int slotTableBytes = Order * 8;      // SLOT_SIZE = 8
            return 16 + kidTableBytes + slotTableBytes; // HEADER_SIZE = 16
        }

        /// <summary>
        /// Computes the maximum payload byte budget for a leaf page based on available heap space and LeafFill.
        /// </summary>
        private int GetMaxLeafPayloadBytes()
        {
            int physicalDataHeapBytes = Math.Max(0, PageSize - GetDataAreaStart());
            return Math.Max(64, (int)(physicalDataHeapBytes * LeafFill));
        }

        /// <summary>
        /// Determines whether all elements in start to end fit within the leaf key limit and payload byte budget.
        /// </summary>
        private bool FitsInLeafBudget(List<Element> keys, int start, int end)
        {
            int keyCount = end - start + 1;
            if (keyCount > Order - 1) return false;

            int payloadBudget = GetMaxLeafPayloadBytes();
            int usedPayload = 0;

            for (int i = start; i <= end; i++)
            {
                int strBytes = System.Text.Encoding.UTF8.GetByteCount(keys[i].Data ?? string.Empty);
                int recordBytes = sizeof(int) + strBytes;
                if (usedPayload + recordBytes > payloadBudget)
                    return false;

                usedPayload += recordBytes;
            }

            return true;
        }

        /// <summary>
        /// Finds the inclusive end index of a contiguous leaf run starting at start.
        /// </summary>
        private int FindLeafRunEnd(List<Element> keys, int start, int end)
        {
            int maxKeys = Math.Max(1, LeafTarget);
            int payloadBudget = GetMaxLeafPayloadBytes();
            int usedPayload = 0;
            int current = start;

            while (current <= end && (current - start) < maxKeys)
            {
                int strBytes = System.Text.Encoding.UTF8.GetByteCount(keys[current].Data ?? string.Empty);
                int recordBytes = sizeof(int) + strBytes;

                if (current > start && usedPayload + recordBytes > payloadBudget)
                    break;

                usedPayload += recordBytes;
                current++;
            }

            return current - 1;
        }

        /// <summary>
        /// Links leaf into the horizontal sibling chain after LastLeaf and stages updates.
        /// </summary>
        private void LinkLeaf(BNode leaf)
        {
            if (LastLeaf != null)
            {
                LastLeaf.NextLeafId = leaf.Id;
                BufferedSave(LastLeaf);
            }
            LastLeaf = leaf;
        }

        /// <summary>
        /// Builds internal index levels bottom-up over levelNodes until a single root node remains.
        /// </summary>
        private BNode BuildInternalLevels(List<(int FirstKey, BNode Node)> levelNodes)
        {
            while (levelNodes.Count > 1)
            {
                var nextLevel = new List<(int FirstKey, BNode Node)>();
                int idx = 0;

                while (idx < levelNodes.Count)
                {
                    BNode parent = Manager.CreateNode(false);
                    parent.SetChildId(0, levelNodes[idx].Node.Id);

                    int groupCount = 1;
                    while (idx + groupCount < levelNodes.Count
                           && parent.NumKeys < Order
                           && !parent.IsFull(string.Empty))
                    {
                        var entry = levelNodes[idx + groupCount];
                        parent.InsertAt(parent.NumKeys, entry.FirstKey, string.Empty, entry.Node.Id);
                        groupCount++;
                    }

                    BufferedSave(parent);
                    nextLevel.Add((levelNodes[idx].FirstKey, parent));
                    idx += groupCount;
                }

                levelNodes = nextLevel;
            }

            return levelNodes[0].Node;
        }

    }
}
