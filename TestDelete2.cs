using SparrowTwo;
using UnitTestOne;

namespace UnitTestTwo
{
    [TestClass]
    public sealed class UnitTwo
    {

        [TestMethod]
        public void SimpleDeleteTest()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    // 1. Create keys.
                    int totalCount = 20;
                    int[] data = Enumerable.Range(1, totalCount).ToArray();

                    // 2. Insert keys.
                    foreach (int k in data)
                    {
                        tree.Insert(new Element { Key = k, Data = String.Empty });
                    }

                    // 3. Verify Root Exists.
                    Assert.IsTrue(tree.Header.RootId >= 0, "Root");

                    // 4. Count Keys
                    int keyCount = tree.CountKeys();
                    Assert.AreEqual(totalCount, keyCount);

                    // 5. Delete three keys.
                    tree.Delete(5);
                    tree.Delete(10);
                    tree.Delete(15);

                    // 6. Verify count after deletions.
                    keyCount = tree.CountKeys();
                    Assert.AreEqual(totalCount - 3, keyCount);

                    // 7. Verify the keys have been deleted.
                    Element e;
                    Assert.IsFalse(tree.TrySearch(5, out e), "Key 5 should be deleted");
                    Assert.IsFalse(tree.TrySearch(10, out e), "Key 10 should be deleted");
                    Assert.IsFalse(tree.TrySearch(15, out e), "Key 15 should be deleted");

                    // 8. Full Audit.
                    var report = tree.PerformFullAudit();
                    Assert.IsTrue(report.Height <= 3, "Height must be 3 or less.");
                    Assert.IsTrue(report.ReachableNodes < 150);
                    Assert.IsTrue(report.TotalKeys < 150);
                    Assert.AreEqual(0, report.ZombieCount, "Zombies");
                    Assert.AreEqual(0, report.GhostCount, "Ghosts");
                    if (tree.GetNodeCount() > 10)
                        Assert.IsTrue(report.AverageDensity > 25.0, "Low density");

                    // 9. Integrity Check.
                    tree.ValidateIntegrity();

                    // 10. Delete all keys.
                    for (int k = 1; k <= totalCount; k++)
                    {
                        tree.Delete(k);
                    }

                    Assert.AreEqual(0, tree.CountKeys(), "Tree should be empty");
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }


        [TestMethod]
        public void SimpleDeleteFirstTest()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    // 1. Create keys.
                    int totalCount = 20;
                    int[] data = Enumerable.Range(1, totalCount).ToArray();

                    // 2. Insert keys.
                    foreach (int i in data)
                    {
                        tree.Insert(new Element { Key = i, Data = Util.NextString(1, 20) });
                    }

                    // 3. Verify Root Exists.
                    Assert.IsTrue(tree.Header.RootId >= 0, "Root");

                    // 4. Count Keys
                    int keyCount = tree.CountKeys();
                    Assert.AreEqual(totalCount, keyCount);

                    // 5. Delete all keys.
                    for (int i = 0; i < data.Length; i++)
                    {
                        tree.DeleteFirst();
                    }

                    Assert.AreEqual(0, tree.CountKeys(), "Tree should be empty");

                    // 6. Integrity Check.
                    tree.ValidateIntegrity();
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        public void SimpleDeleteLastTest()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    // 1. Create keys.
                    int totalCount = 20;
                    int[] data = Enumerable.Range(1, totalCount).ToArray();

                    // 2. Insert keys.
                    foreach (int k in data)
                    {
                        tree.Insert(new Element { Key = k, Data = Util.NextString(1, 20) });
                    }

                    // 3. Verify Root Exists.
                    Assert.IsTrue(tree.Header.RootId >= 0, "Root");

                    // 4. Count Keys
                    int keyCount = tree.CountKeys();
                    Assert.AreEqual(totalCount, keyCount);

                    // 5. Delete all keys.
                    for (int k = 0; k < totalCount; k++)
                    {
                        tree.DeleteLast();
                    }

                    Assert.AreEqual(0, tree.CountKeys(), "Tree should be empty");

                    // 6. Integrity Check.
                    tree.ValidateIntegrity();
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        public void TestDeleteMiddleOut()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    int total = 100;
                    for (int i = 1; i <= total; i++) tree.Insert(new Element { Key = i, Data = Util.NextString(1, 20) });

                    // Delete from the middle moving outwards
                    int mid = total / 2;
                    for (int offset = 0; offset < mid; offset++)
                    {
                        tree.Delete(mid - offset);
                        tree.Delete(mid + offset + 1);
                        tree.ValidateIntegrity();
                    }

                    Assert.AreEqual(0, tree.CountKeys(), "Tree should be empty.");
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        public void TestDeleteRandomShuffle()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    var keys = Enumerable.Range(1, 100).ToList();
                    foreach (var k in keys) tree.Insert(k, Util.NextString(1, 20));

                    var random = new Random();
                    var shuffle = keys.OrderBy(x => random.Next()).ToList();

                    foreach (var k in shuffle)
                    {
                        tree.Delete(k);

                        Element e;
                        Assert.IsFalse(tree.TrySearch(k, out e), $"Key {k} should be gone.");
                    }

                    Assert.AreEqual(0, tree.CountKeys());
                    tree.ValidateIntegrity();
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        public void TestDeleteNonExistentKey()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    tree.Insert(10, Util.NextString(1, 20));
                    tree.Insert(20, Util.NextString(1, 20));

                    tree.Delete(5);
                    tree.Delete(15);
                    tree.Delete(25);

                    Assert.AreEqual(2, tree.CountKeys(), "Tree count should remain unchanged.");
                    tree.ValidateIntegrity();
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        public void TestSequentialDeleteThousand()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    int count = 1000;

                    // 1. Bulk Insert
                    for (int i = 1; i <= count; i++)
                    {
                        tree.Insert(i, Util.NextString(1, 20));
                    }
                    Assert.AreEqual(count, tree.CountKeys(), "Failed initial insert count.");

                    // 2. Sequential Delete
                    for (int i = 1; i <= count; i++)
                    {
                        tree.Delete(i);

                        // Check integrity every 1000 deletes to save time but ensure safety
                        if (i % 100 == 0)
                        {
                            tree.ValidateIntegrity();
                        }
                    }

                    Assert.AreEqual(0, tree.CountKeys(), "Tree should be empty.");
                    tree.ValidateIntegrity();
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        public void TestSequentialDeleteOneHundred()
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    int count = 100;

                    // 1. Bulk Insert
                    for (int i = 1; i <= count; i++)
                    {
                        tree.Insert(i, Util.NextString(1, 20));
                        tree.ValidateIntegrity();
                    }

                    tree.ValidateIntegrity();

                    // 2. Sequential Delete
                    for (int i = 1; i <= count; i++)
                    {
                        tree.Delete(i);
                        tree.ValidateIntegrity();
                    }

                    Assert.AreEqual(0, tree.CountKeys(), "Tree should be empty.");
                    tree.ValidateIntegrity();
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }


        [TestMethod]
        [DataRow(7)]
        [DataRow(109)]
        [DataRow(223)]
        [DataRow(341)]
        [DataRow(551)]
        public void TestRandomDeleteThousand(int seed)
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                using (var tree = new BTree(outFile, 256))
                {
                    int count = 1000;
                    var keys = Enumerable.Range(1, count).ToList();
                    var random = new Random(seed);

                    // 1. Insert in random order
                    var insertOrder = keys.OrderBy(x => random.Next()).ToList();
                    foreach (var k in insertOrder)
                    {
                        tree.AddOrUpdate(k, Util.NextString(1, 20));
                    }

                    // 2. Delete in a different random order
                    var deleteOrder = keys.OrderBy(x => random.Next()).ToList();
                    foreach (var k in deleteOrder)
                    {
                        tree.Delete(k);
                    }

                    Assert.AreEqual(0, tree.CountKeys(), "Tree should be empty.");

                    // Final Audit
                    var report = tree.PerformFullAudit();
                    Assert.AreEqual(0, report.TotalKeys, "Missing Keys");
                    Assert.AreEqual(0, report.ZombieCount, "Zombies");
                    Assert.AreEqual(0, report.GhostCount, "Ghosts");
                }
            }
            finally
            {
                File.Delete(outFile);
            }
        }

        [TestMethod]
        [DataRow(15)]
        [DataRow(33)]
        [DataRow(42)]
        [DataRow(57)]
        [DataRow(71)]
        public void TestRandomDeleteHundred(int seed)
        {
            string outFile = TestHelper.GetTempDb();
            File.Delete(outFile);

            try
            {
                // 1. Generate or use a specific seed. 
                // To reproduce a failure, replace Environment.TickCount with the seed from your test output window.
                var random = new Random(seed);

                // Log tracking collections to dump if an assertion fails
                var insertOrder = new List<int>();
                var deleteOrder = new List<int>();
                var completedDeletions = new List<int>();

                using (var tree = new BTree(outFile, 256))
                {
                    tree.Clear();

                    int count = 200;
                    var keys = Enumerable.Range(1, count).ToList();

                    // Populate insertion sequence
                    insertOrder = keys.OrderBy(x => random.Next()).ToList();
                    foreach (var k in insertOrder)
                    {
                        tree.AddOrUpdate(k, Util.NextString(1, 20));
                    }

                    // Populate deletion sequence
                    deleteOrder = keys.OrderBy(x => random.Next()).ToList();

                    // Get the key that failed.
                    foreach (var k in deleteOrder)
                    {
                        tree.Delete(k);
                        tree.ValidateIntegrity();
                    }

                    // Check if tree is empty.
                    int remainingKeys = tree.CountKeys();
                    if (remainingKeys != 0)
                    {
                        Assert.Fail($"Tree should be empty but has {remainingKeys} items left.\n" +
                                    $"Insert Order: {string.Join(", ", insertOrder)}\n" +
                                    $"Delete Order: {string.Join(", ", deleteOrder)}");
                    }

                    var report = tree.PerformFullAudit();
                    if (report.TotalKeys != 0 || report.ZombieCount != 0 || report.GhostCount != 0)
                    {
                        Assert.Fail($"Audit failed.\n" +
                                    $"Zombies: {report.ZombieCount}, Ghosts: {report.GhostCount}\n" +
                                    $"Insert Order: {string.Join(", ", insertOrder)}\n" +
                                    $"Delete Order: {string.Join(", ", deleteOrder)}");
                    }
                }
            }
            finally
            {

                File.Delete(outFile);
            }
        }
    }
}
