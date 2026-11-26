using SparrowTwo;
using UnitTestOne;

namespace UnitTestFour
{

    [TestClass]
    public sealed class UnitFour
    {
        private bool IsDebugStress = TestSettings.CanDebugStress;


        [TestMethod]
        public void IntegrityCheckOne()
        {
            string path = TestHelper.GetTempDb();
            File.Delete(path);

            try
            {
                using (var tree = new BTree(path))
                {
                    // 1. Build a specific structure: Root with two children
                    // Inserting 10, 20, 30.
                    // We insert more to force a split.
                    int[] keys = { 10, 20, 30, 40, 50, 60 };
                    foreach (var k in keys) tree.Insert(k, Util.NextString(1, 20));

                    // 2. Perform a deletion that triggers MergeChildren
                    // We delete keys until a node hits t-1 and its sibling is also thin.
                    tree.Delete(60);
                    tree.Delete(50);

                    int count = tree.CountKeys();
                    Assert.AreEqual(4, count, "Missing Keys.");

                    // 3. Run Integrity Check prior to compaction
                    // This ensures Case 3 didn't leave orphaned IDs in Kids[]
                    tree.ValidateIntegrity();

                    // 4. Verify physical space management
                    // Ensure deleted node IDs were pushed to FreeList.
                    tree.Compact();
                    Assert.IsTrue(tree.Header.NodeCount < 5, "Compaction failed.");

                    // Zombies
                    Assert.AreEqual(0, tree.CountZombies(), "Zombies");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void CompactTestOne()
        {
            string path = TestHelper.GetTempDb();
            File.Delete(path);

            try
            {
                int count = 1000;
                int deleteCount = 800;

                using (var tree = new BTree(path, 256))
                {
                    var random = new Random(42);
                    var expected = new Dictionary<int, string>();

                    while (expected.Count < count)
                    {
                        int k = random.Next(1, 100_000);
                        if (!expected.ContainsKey(k))
                        {
                            string val = Util.NextString(1, 20);
                            expected[k] = val;
                            tree.Insert(k, val);
                        }
                    }

                    // Delete a significant portion to create major fragmentation holes in the file.
                    var keysToDelete = expected.Keys.Take(deleteCount).ToList();
                    foreach (var k in keysToDelete)
                    {
                        tree.Delete(k);
                        expected.Remove(k);
                    }

                    tree.ValidateIntegrity();

                    long sizeBefore = new FileInfo(path).Length;

                    // COMPACT
                    tree.Compact();

                    long sizeAfter = new FileInfo(path).Length;

                    // 1. PHYSICAL ASSERT: File must be smaller.
                    Assert.IsTrue(sizeAfter < sizeBefore, $"Compaction failed. Before: {sizeBefore}, After: {sizeAfter}");

                    // 2. INTEGRITY ASSERT: Root must be valid.
                    Assert.IsFalse(tree.Header.RootId < 0, "Root lost");

                    // 3. DATA ASSERT: Every remaining key must still be searchable and correct.
                    foreach (var kvp in expected)
                    {
                        Element result;
                        Assert.IsTrue(tree.TrySearch(kvp.Key, out result), $"Key {kvp.Key} missing");
                        Assert.AreEqual(kvp.Value, result.Data, "Corrupted");
                    }

                    // 4. STRUCTURE ASSERT: Ensure the B+ Tree logic still holds
                    tree.CheckGhost();
                    count = tree.CountKeys();
                    Assert.AreEqual(expected.Count, count, "Missing Keys");
                    Assert.AreEqual(0, tree.CountZombies(), "Zombies");

                    // 5. LEAF CHAIN ASSERT: Verify horizontal integrity
                    // If NextLeafId mapping failed, this will crash or return a wrong count.
                    int leafLinkCount = tree.GetLeafChainCount();
                    Assert.IsTrue(leafLinkCount > 0, "Leaf chain broken");

                    // 6. FREELIST ASSERT: Ensure the holes were truly welded shut.
                    Assert.AreEqual(0, tree.Header.FreeListCount, "FreeList not cleared");
                    Assert.AreEqual(0, tree.Header.FreeListOffset, "FreeListOffset should be 0.");

                    // 7. SORTED ORDER ASSERT: Walk the leaf chain and ensure keys are strictly increasing.
                    int? lastKey = null;
                    var sortedList = tree.GetKeys();
                    foreach (var currentKey in sortedList)
                    {
                        if (lastKey.HasValue)
                        {
                            Assert.IsTrue(currentKey > lastKey.Value, $"Sort Violation: Key {currentKey} is not greater than preceding key {lastKey.Value}");
                        }
                        lastKey = currentKey;
                    }
                }
            }
            finally
            {
                File.Delete(path);
            }
        }


        [TestMethod]
        public void CompactPreservesData()
        {
            if (!IsDebugStress) return;

            string path = TestHelper.GetTempDb();
            File.Delete(path);

            try
            {
                int count = 1000;
                int deleteCount = 100;

                var rnd = new Random(42);
                var expected = new Dictionary<int, string>();

                using (var tree = new BTree(path))
                {
                    while (expected.Count < count)
                    {
                        int k = rnd.Next(1, 1000_000);
                        if (!expected.ContainsKey(k))
                        {
                            string val = Util.NextString(1, 20);
                            expected[k] = val;
                            tree.Insert(k, val);
                        }
                    }

                    // Delete some keys
                    var toDelete = expected.Keys.Take(deleteCount).ToList();
                    foreach (var k in toDelete)
                    {
                        tree.Delete(k);
                        expected.Remove(k);
                    }

                    tree.ValidateIntegrity();
                    long before = new FileInfo(path).Length;

                    tree.Compact();

                    long after = new FileInfo(path).Length;

                    // File should shrink and data preserved
                    Assert.IsTrue(after <= before, $"Compact did not shrink file: before={before} after={after}");

                    // All remaining keys and values must still match
                    foreach (var kvp in expected)
                    {
                        Element e;
                        Assert.IsTrue(tree.TrySearch(kvp.Key, out e), $"Missing key after compact: {kvp.Key}");
                        Assert.AreEqual(kvp.Value, e.Data);
                    }

                    tree.ValidateIntegrity();
                    Assert.AreEqual(0, tree.CountZombies(), "Zombies present after compact");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

    }
}
