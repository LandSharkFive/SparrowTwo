using SparrowTwo;

namespace UnitTestOne
{
    [TestClass]
    public sealed class UnitOne
    {

        [TestMethod]
        public void SimpleInsertTest()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                int[] keysToInsert = { 10, 20, 30, 40, 50, 25 };
                int totalCount = keysToInsert.Length;
                int[] sortedKeys = keysToInsert.OrderBy(k => k).ToArray();
                var expected = new Dictionary<int, string>();

                using (var tree = new BTree(outFile, 256))
                {
                    // Insert keys in an order that forces splits with distinct payloads.
                    foreach (int k in keysToInsert)
                    {
                        string payload = Util.NextString(1, 20);
                        expected[k] = payload;
                        tree.Insert(new Element { Key = k, Data = payload });
                    }

                    // 1. Verify Root Exists.
                    Assert.IsTrue(tree.Header.RootId >= 0, "Root");

                    // 2. Count Keys
                    int keyCount = tree.CountKeys();
                    Assert.AreEqual(totalCount, keyCount);

                    // 3. Get Keys and Verify Order & Uniqueness.
                    var allKeys = tree.GetKeys();
                    Assert.AreEqual(totalCount, allKeys.Count);
                    Assert.IsTrue(allKeys.SequenceEqual(sortedKeys));
                    Assert.IsTrue(Util.IsSorted(allKeys));
                    Assert.IsFalse(Util.HasDuplicate(allKeys));

                    // 4. Test Search
                    int searchKey = 30;
                    Assert.IsTrue(tree.TrySearch(searchKey, out Element found));
                    Assert.AreEqual(searchKey, found.Key);
                    Assert.AreEqual(expected[searchKey], found.Data);

                    // 5. Search every key and verify its exact payload.
                    foreach (int k in allKeys)
                    {
                        Assert.IsTrue(tree.TrySearch(k, out var e), $"Missing key {k}.");
                        Assert.AreEqual(expected[k], e.Data, $"Payload mismatch for key {k}.");
                    }
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }


        [TestMethod]
        public void TestSequential()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                var expected = new Dictionary<int, string>();
                const int count = 100;

                using (var tree = new BTree(outFile, 256))
                {
                    // 1. Insert sequential keys with variable-length random payloads.
                    for (int i = 1; i <= count; i++)
                    {
                        string payload = Util.NextString(1, 20);
                        expected[i] = payload;
                        tree.Insert(new Element(i, payload));
                    }

                    // 2. Verify Root Exists.
                    Assert.IsTrue(tree.Header.RootId >= 0, $"Root missing.");

                    // 3. Count Keys & Leaf Chain Consistency.
                    int keyCount = tree.CountKeys();
                    Assert.AreEqual(count, keyCount);
                    Assert.AreEqual(keyCount, tree.GetLeafChainCount(), $"Leaf chain count mismatch.");

                    // 4. Get Keys and Verify Order & Uniqueness.
                    var allKeys = tree.GetKeys();
                    Assert.AreEqual(count, allKeys.Count);
                    Assert.IsTrue(Util.IsSorted(allKeys));
                    Assert.IsFalse(Util.HasDuplicate(allKeys));

                    // 5. Verify Tree Height.
                    // For Order 4, 100 items should be about 4 levels deep.
                    int height = tree.GetHeight();
                    Assert.IsTrue(height <= 3, $"Height must be 3 or less.");

                    // 6. Spot Check Search.
                    int searchKey = 50;
                    Assert.IsTrue(tree.TrySearch(searchKey, out Element result));
                    Assert.AreEqual(searchKey, result.Key);
                    Assert.AreEqual(expected[searchKey], result.Data);

                    // 7. Search every key and verify its exact random payload.
                    foreach (int k in allKeys)
                    {
                        Assert.IsTrue(tree.TrySearch(k, out var e), $"Missing key {k}.");
                        Assert.AreEqual(expected[k], e.Data, $"Payload mismatch for key {k}.");
                    }

                    // 8. Full Audit.
                    var report = tree.PerformFullAudit();
                    Assert.IsTrue(report.Height <= 3, $"Audit height exceeded 3.");
                    Assert.AreEqual(count, report.TotalKeys);
                    Assert.AreEqual(0, report.ZombieCount, $"Zombies detected.");
                    Assert.AreEqual(0, report.GhostCount, $"Ghosts detected.");
                    if (tree.GetNodeCount() > 10)
                        Assert.IsTrue(report.AverageDensity > 25.0, $"Low density.");

                    // 9. Integrity Check.
                    tree.ValidateIntegrity();
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }


        [TestMethod]
        public void RandomInsertionTest()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                int seed = 42;
                Random rnd = new Random(seed);
                var expected = new Dictionary<int, string>();
                const int count = 100;

                using (var tree = new BTree(outFile, 256))
                {
                    // 1. Insert random keys with variable-length random payloads.
                    while (expected.Count < count)
                    {
                        int nextKey = rnd.Next(1, 10_000);
                        if (!expected.ContainsKey(nextKey))
                        {
                            string payload = Util.NextString(1, 20);
                            expected[nextKey] = payload;
                            tree.Insert(nextKey, payload);
                        }
                    }

                    // 2. Verify Root Exists.
                    Assert.IsTrue(tree.Header.RootId >= 0, $"Root missing.");

                    // 3. Count Keys & Leaf Chain Consistency.
                    Assert.AreEqual(count, tree.CountKeys());
                    Assert.AreEqual(tree.CountKeys(), tree.GetLeafChainCount());

                    // 4. Get Keys and Verify Order & Uniqueness.
                    List<int> allKeys = tree.GetKeys();
                    Assert.AreEqual(count, allKeys.Count);
                    Assert.IsTrue(Util.IsSorted(allKeys));
                    Assert.IsFalse(Util.HasDuplicate(allKeys));

                    // 5. Verify Tree Height.
                    Assert.IsTrue(tree.GetHeight() <= 3, $"Height exceeded 3.");

                    // 6. Search every key and verify its exact random payload.
                    foreach (int k in allKeys)
                    {
                        Assert.IsTrue(tree.TrySearch(k, out Element e), $"Missing key {k}.");
                        Assert.AreEqual(expected[k], e.Data, $"Payload mismatch for key {k}.");
                    }

                    // 7. Full Audit.
                    var report = tree.PerformFullAudit();
                    Assert.IsTrue(report.Height <= 3, $"Audit height exceeded 3.");
                    Assert.AreEqual(count, report.TotalKeys);
                    Assert.AreEqual(0, report.ZombieCount, $"Zombies detected.");
                    Assert.AreEqual(0, report.GhostCount, $"Ghosts detected.");
                    if (tree.GetNodeCount() > 10)
                    {
                        Assert.IsTrue(report.AverageDensity > 25.0, $"Low density.");
                    }

                    // 8. Integrity Check.
                    tree.ValidateIntegrity();
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        public void TestPersistence()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                int[] data = { 5, 15, 25, 35, 45 };
                var expected = new Dictionary<int, string>();

                // Phase 1: Write and Close.
                using (var tree = new BTree(outFile, 256))
                {
                    foreach (int x in data)
                    {
                        string payload = Util.NextString(1, 20);
                        expected[x] = payload;
                        tree.Insert(x, payload);
                    }
                }

                // Phase 2: Re-open and Verify.
                using (var tree = new BTree(outFile))
                {
                    foreach (int x in data)
                    {
                        Assert.IsTrue(tree.TrySearch(x, out var result), $"Key {x} lost after reload.");
                        Assert.AreEqual(expected[x], result.Data, $"Payload mismatch for key {x} after reload.");
                    }
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }


        [TestMethod]
        public void TestRangeScanStandard()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                // Use a small order to ensure multiple leaves and a linked chain
                using (var tree = new BTree(outFile, 256))
                {
                    // Insert 10, 20, 30, 40, 50, 60.
                    for (int i = 10; i <= 60; i += 10)
                    {
                        string payload = Util.NextString(1, 20);
                        tree.Insert(i, payload);
                    }

                    // Test: Range 25 to 55.
                    // Should find: 30, 40, 50.
                    var results = tree.GetKeyRange(25, 55);

                    Assert.AreEqual(3, results.Count);
                    Assert.AreEqual(30, results[0]);
                    Assert.AreEqual(40, results[1]);
                    Assert.AreEqual(50, results[2]);
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        public void TestRangeScanEdgeCases()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    for (int i = 10; i <= 30; i += 10)
                    {
                        string payload = Util.NextString(1, 20);
                        tree.Insert(i, payload);
                    }

                    // 1. Range entirely below existing keys
                    var below = tree.GetKeyRange(0, 5);
                    Assert.AreEqual(0, below.Count);

                    // 2. Range entirely above existing keys
                    var above = tree.GetKeyRange(40, 50);
                    Assert.AreEqual(0, above.Count);

                    // 3. Range that covers everything
                    var all = tree.GetKeyRange(0, 100);
                    Assert.AreEqual(3, all.Count);

                    // 4. Single item range
                    var single = tree.GetKeyRange(20, 20);
                    Assert.AreEqual(1, single.Count);
                    Assert.AreEqual(20, single[0]);
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        public void TestBoundaries()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    int[] keys = { 50, 10, 30, 20, 40 };
                    foreach (var k in keys)
                    {
                        string payload = Util.NextString(1, 20);
                        tree.Insert(k, payload);
                    }

                    Assert.AreEqual(10, tree.SelectFirst().Key);
                    Assert.AreEqual(50, tree.SelectLast().Key);
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }


        [TestMethod]
        public void TestUpdateValue()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    // 1. Setup: Insert initial data
                    tree.Insert(10, "Jack");
                    tree.Insert(20, "Jill");
                    tree.Insert(30, "Bob");

                    // 2. Execute: Update the value for an existing key
                    bool wasUpdated = tree.UpdateValue(20, "Harry");
                    Assert.IsTrue(wasUpdated, "UpdateValue should return true for existing key.");

                    // 3. Verify: Search to confirm the data changed
                    tree.TrySearch(20, out var result);
                    Assert.AreEqual("Harry", result.Data, "The data for key 20 was not updated correctly.");

                    // 4. Persistence Check: Close and reopen to ensure DiskWrite happened
                }

                using (var tree = new BTree(outFile))
                {
                    tree.TrySearch(20, out var result);
                    Assert.AreEqual("Harry", result.Data, "Updated value did not persist to disk.");

                    // 5. Edge Case: Attempt to update a non-existent key
                    bool failedUpdate = tree.UpdateValue(99, "Anthony");
                    Assert.IsFalse(failedUpdate, "UpdateValue should return false for missing key.");
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        public void TestSave()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    // 1. Setup: Initial data
                    tree.Insert(10, "Jack");
                    tree.Insert(20, "Jill");

                    // 2. Save: Update existing key (20)
                    tree.AddOrUpdate(20, "Bob");

                    // 3. Save: Insert brand new key (30)
                    tree.AddOrUpdate(30, "Harry");

                    // Verify intermediate state
                    bool found20 = tree.TrySearch(20, out var res20);
                    bool found30 = tree.TrySearch(30, out var res30);

                    Assert.IsTrue(found20, "Key 20 should exist.");
                    Assert.AreEqual("Bob", res20.Data, "Save failed to update existing key 20.");
                    Assert.IsTrue(found30, "Key 30 should have been inserted by Save.");
                    Assert.AreEqual("Harry", res30.Data, "Save failed to insert new key 30.");
                }

                // 4. Persistence Check: Re-open the file
                using (var tree = new BTree(outFile))
                {
                    bool found20 = tree.TrySearch(20, out var res20);
                    bool found30 = tree.TrySearch(30, out var res30);

                    Assert.AreEqual("Bob", res20.Data, "Updated value Bob did not persist.");
                    Assert.AreEqual("Harry", res30.Data, "Inserted value Harry did not persist.");
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

    }
}
