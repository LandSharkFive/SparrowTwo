# SparrowTwo

**SparrowTwo** is a lightweight, high-performance, disk-resident **B+ Tree** storage engine written in C#. Designed for predictable binary I/O and variable-length data payloads, it implements slotted-page node architecture, single-pass rebalancing, horizontal leaf-chain range scans, free-list page reclamation, and comprehensive structural health auditing.


## Key Features

* **Disk-Resident Slotted-Page Architecture (BNode)**: Stores 32-bit integer keys paired with variable-length UTF-8 string payloads inside fixed-size disk pages (minimum 256 bytes, default 4096 bytes). Includes automatic on-page defragmentation (Compact) when variable-length updates cause heap fragmentation.
* **Single-Pass Balancing (BTree)**: 
  * **Insertions**: Uses proactive top-down splitting during traversal to guarantee parent nodes always have room for child splits without backtracking.
  * **Deletions**: Uses proactive borrowing (left/right sibling rotations) and merging during descent to maintain minimum occupancy invariants in a single pass, complete with automatic root collapse when tree height shrinks.
* **Fast Sequential & Range Scans**: Leaf nodes are horizontally linked via NextLeafId pointers, enabling fast sequential traversal (EnumerateRange, GetKeyRange, GetElementRange) without revisiting internal index levels.
* **Bottom-Up Bulk Loading (TreeBuilder)**: Rapidly constructs balanced B+ Trees from pre-sorted datasets using write coalescing (BufferedSave) and configurable page fill factors (LeafFill).
* **Space Reclamation & File Compaction**:
  * **FreeList Recycling**: Decommissioned page IDs are zeroed out, tracked in memory, and persisted to the file tail on disposal (Dispose), allowing immediate page reuse without growing the file.
  * **Full File Compaction (BTree.Compact())**: Rebuilds the entire database into a contiguous temporary file using a live-node reachability scan and atomic file swap to eliminate physical file fragmentation.
* **Built-In Integrity Auditing & Diagnostics**: Includes tools to detect cycle corruption, unreachable orphaned pages (Zombies), dangling pointers (Ghosts), and sorting violations (PerformFullAudit, ValidateIntegrity, TracePath).


## Architecture & Physical Layout

### File Structure
Every SparrowTwo database file begins with a reserved 4,096-byte Header (BTreeHeader), followed sequentially by fixed-width pages indexed from 0 to NodeCount - 1. When closed cleanly, any recycled page IDs in the FreeList are serialized to the tail of the file and truncated upon reopening.

```text
+-------------------+-------------------+-------------------+-----+-------------------+
|   File Header     |      Page 0       |      Page 1       | ... |   FreeList Tail   |
|   (4096 bytes)    |  (PageSize bytes) |  (PageSize bytes) |     |  (4 bytes / ID)   |
+-------------------+-------------------+-------------------+-----+-------------------+
```

### Slotted-Page Node Layout (`BNode`)
Each page uses a slotted-page design where the directory structures grow forward from the top of the page, while variable-length records are appended into the data heap:

```text
+---------------+-----------------------+-------------------+-------------------------+
|  Node Header  |  Child Page IDs       |    Slot Table     |        Data Heap        |
|   (16 bytes)  | ((Order + 1) * 4 B)   |  (Order * 8 B)    | [4B Key + UTF-8 String] |
+---------------+-----------------------+-------------------+-------------------------+
```
* **Node Header (16 bytes)**: Stores Leaf flag (4B), PageId (4B), NumKeys (4B), and NextLeafId (4B).
* **Child Page IDs**: Stores Order + 1 32-bit child page pointers (used in internal nodes).
* **Slot Table**: Stores Order 8-byte directory entries (4B Data Offset + 4B Record Length) kept in sorted key order for O(N log N) binary search.
* **Data Heap**: Stores contiguous records consisting of a 4-byte little-endian integer key followed immediately by raw UTF-8 string bytes.

## Getting Started

## Usage Examples

### 1. Basic CRUD & Range Queries
Always wrap BTree in a using block so that the header and FreeList are properly flushed to disk on disposal.

```csharp
using SparrowTwo;

// Create or open a database with 4096-byte pages
using (var tree = new BTree("my_database.db", pageSize: 4096))
{
    // Insert key-value pairs
    tree.Insert(10, "Alpha");
    tree.Insert(20, "Bravo");
    tree.Insert(30, "Charlie");
    tree.AddOrUpdate(20, "Bravo Updated");

    // Point lookup
    if (tree.TrySearch(20, out Element result))
    {
        Console.WriteLine($"Found Key {result.Key}: {result.Data}");
    }

    // Fast horizontal leaf-chain range scan [10..30]
    foreach (Element item in tree.EnumerateRange(10, 30))
    {
        Console.WriteLine($"{item.Key} -> {item.Data}");
    }

    // Delete a key and reclaim space
    tree.Delete(10);
    tree.Commit();
}
```

### 2. Bulk Loading from Sorted Data
When initializing a database from a large, pre-sorted dataset, use TreeBuilder for optimal build speed and controlled page fill density:

```csharp
using SparrowTwo;

var sortedRecords = new List<Element>
{
    new Element(1, "Record 1"),
    new Element(2, "Record 2"),
    new Element(3, "Record 3")
};

// Build tree with 4096-byte pages and an 80% leaf fill factor
var builder = new TreeBuilder(pageSize: 4096, leafFill: 0.8);
builder.CreateFromSorted("bulk_loaded.db", sortedRecords);
```

### 3. Auditing & Compaction
Inspect the physical health of your database file or compact fragmented pages:

```csharp
using (var tree = new BTree("my_database.db"))
{
    // Verify structural invariants (throws on corruption)
    tree.ValidateIntegrity();

    // Print detailed health metrics (Height, Density, Zombies, Ghosts)
    tree.PrintAuditReport();

    // Rebuild file to eliminate physical gaps and reset the FreeList
    if (tree.CountZombies() > 0 || tree.GetFreeListCount() > 50)
    {
        tree.Compact();
    }
}
```

## License

Copyright (c) 2026 Koivu. Licensed under the MIT License.
