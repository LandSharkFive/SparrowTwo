# Slotted-Page B+ Tree Nodes (BNode)

## Overview

In SparrowTwo, `BNode` implements a Slotted-Page architecture backed directly by a fixed-size `byte[]` page buffer. Instead of allocating managed objects during low-level traversal, `BNode` reads and writes primitives directly into the byte buffer via `BinaryPrimitives` (little-endian) and `Encoding.UTF8`. This allows pages to be flushed to and loaded from disk with zero serialization overhead while supporting variable-length UTF-8 string payloads.

## Physical Page Layout

The byte buffer is partitioned into four contiguous regions. Unlike classic slotted pages where the slot directory grows forward from the header and the data heap grows backward from the end of the page, both the Slot Table and the Data Heap grow forward from fixed starting offsets determined by the B+ Tree `Order`:

```text
+----------------+-----------------------+-----------------+-----------------------+
|     Header     |   Kid Table (Pages)   |   Slot Table    |       Data Heap       |
|   (16 Bytes)   |  ((Order + 1) * 4B)   |  (Order * 8B)   |  (Remaining Bytes)    |
+----------------+-----------------------+-----------------+-----------------------+
0                16                 SlotTableStart   DataAreaStart            PageSize
```

### 1. Header (0 - 15 bytes)
Stores node metadata as four 32-bit little-endian integers (`HEADER_SIZE = 16`):

| Offset | Constant | Size | Description |
| :--- | :--- | :--- | :--- |
| 0 | `OFF_LEAF` | 4B | `1` for a Leaf node; `0` for an Internal node. |
| 4 | `OFF_PAGE_ID` | 4B | Unique page identifier on disk. |
| 8 | `OFF_NUM_KEYS` | 4B | Current number of active keys ($N$). |
| 12 | `OFF_NEXT_LEAF` | 4B | Page ID of the right sibling leaf (`-1` if none or if internal). |

### 2. Kid Table (`KidTableStart = 16`)
* **Size:** `(Order + 1) * 4` bytes (`KID_SIZE = 4`).
* **Purpose:** Stores child `PageId` pointers for internal nodes ($N$ keys require $N + 1$ child branches).
* **Leaf Nodes:** This region is still reserved in leaf buffers so `SlotTableStart` and `DataAreaStart` remain identical across node types, keeping offset calculations uniform.

### 3. Slot Table (`SlotTableStart = 16 + (Order + 1) * 4`)
* **Size:** `Order * 8` bytes (`SLOT_SIZE = 8`).
* **Purpose:** Acts as a sorted indirection directory mapping a logical index (`0` to `NumKeys - 1`) to a physical byte offset and byte length in the Data Heap.
* **Slot Entry Layout (8 bytes):**
  * **Bytes 0–3 (`Int32`):** Absolute byte offset of the record in `Buffer`.
  * **Bytes 4–7 (`Int32`):** Total length of the record in bytes (`4 + UTF8ByteCount`).

### 4. Data Heap (`DataAreaStart = SlotTableStart + Order * 8`)
* **Size:** `PageSize - DataAreaStart` bytes.
* **Purpose:** Stores variable-length key-value records (`Element`), each serialized as a 4-byte little-endian `Int32` key immediately followed by raw UTF-8 string payload bytes (no null terminator or separate length prefix, as payload byte length is derived from `Slot.Length - 4`).
* **Ordering:** Records in the Data Heap are not physically sorted. New or growing records are appended sequentially to the end of the heap (`GetNextFreeDataOffset()`) while the Slot Table maintains logical key sort order.

## Core Operations

### Search & Navigation
* **Leaf Lookup (`FindIndex`):** Binary searches keys via `GetKey(mid)` (which resolves `Slot[mid]` heap offset). Returns the matching index or the bitwise complement (`~low`) of the insertion point.
* **Internal Routing (`FindChildBranchIndex`):** Binary searches separator keys so that `key < Key[i]` routes to `Kid[i]` and `key >= Key[i]` routes to `Kid[i + 1]`.

### Insertion & In-Place Updates (`InsertAt`, `SetElementFixed`)
* **Capacity Guard (`IsFull(string data)`):** Checks both logical capacity (`NumKeys >= Order`) and physical byte capacity (`DataAreaStart + compactedHeapBytes + incomingRecordBytes <= PageSize`). `BTree` uses this guard for proactive top-down splitting and update-induced splits.
* **`InsertAt(pos, key, data, childPageId)`:**
  1. Computes `recordBytes = 4 + Encoding.UTF8.GetByteCount(data)` and checks tail space via `GetNextFreeDataOffset()`. If fragmented, triggers `Compact()` before writing.
  2. Shifts existing slot entries from `pos` onward right by `SLOT_SIZE` (`Buffer.BlockCopy`).
  3. For internal nodes, shifts child pointers right via `ShiftChildrenRight(pos)` and writes `childPageId` into `Kid[pos + 1]`.
  4. Appends the 4-byte key + UTF-8 payload to the Data Heap and writes its `(offset, recordBytes)` pair into `Slot[pos]`.
  5. Increments `NumKeys`.
* **`SetElementFixed(index, key, data)`:** Updates a record in place if `newRecordBytes <= currentRecordBytes` (zeroing any leftover trailing bytes); if the string payload grows larger, zeroes the old record, appends to the tail (compacting first if needed), and updates the slot's offset and length.

### Deletion, Merging & Compaction (`RemoveKeyAndChildAt`, `Compact`)
* Removing a key shifts the Slot Table (and Kid Table, if internal) left by one slot, clears the trailing dead slot entry, resets unused child pointers to `-1`, and runs `Compact()`.
* `Compact()` defragments the page by extracting active `Element` pairs in logical order (`GetAllElements()`), rewriting them contiguously starting at `DataAreaStart`, updating every slot's `(offset, length)` pair, and zeroing out stale tail bytes.
* During `BTree` deletion, `MergeChildren` checks `CanMergeIntoLeft` before merging two siblings so that variable-length payloads exceeding a single page's physical byte capacity do not overflow the target page.

### Node Splitting (`Split`)
* **Leaf Split:** Copies the middle `separatorKey` (`midIndex = NumKeys / 2`) up to the parent (with an empty string payload) while retaining the full record in the right sibling leaf, then updates the `NextLeafId` singly-linked list pointers.
* **Internal Split:** Promotes `separatorKey` to the parent and removes it from both children to preserve the $N$ keys, $N + 1$ children invariant. Right-hand child pointers are bulk-transferred via `CopyChildrenTo`, and stale pointers on the left node are cleared to `-1`.

## Critical Invariants & Edge Cases
* **Unused Child Pointers Must Be `-1`:** Never zero out inactive child slots with `0` or `.Clear()`, because `0` is a valid `PageId`. Always use `SetChildId(index, -1)` or `ClearChildPointers()`.
* **Dual Capacity Constraints (Logical Order vs. Physical Bytes):** With variable-length UTF-8 strings, a node can reach physical byte capacity before reaching `Order` keys (or vice versa). Always check `node.IsFull(data)` rather than `node.NumKeys >= Order` alone before inserting or growing a record.
* **Baseline Order Sizing:** `BNode.CalculateMaxOrder(pageSize)` computes baseline `Order` assuming 8-byte minimum records (`RECORD_SIZE = 8`, `SLOT_SIZE = 8`, `KID_SIZE = 4`) plus fixed header/kid overhead.