using SparrowTwo;
using UnitTestOne;

namespace UnitTestNine
{

    [TestClass]
    public sealed class UnitNine
    {
        [TestMethod]
        public void WriteToFileTest()
        {
            // 1. Setup: Use a real temporary file name
            string path = TestHelper.GetTempDb();
            string tempFile = "export.txt";

            File.Delete(path);
            File.Delete(tempFile);

            try
            {
                using (var tree = new BTree(path, 256))
                {

                    // Fill the tree with enough data to force multiple leaves (e.g., 20 keys)
                    for (int i = 1; i <= 20; i++)
                    {
                        tree.Insert(i, Util.NextString(1, 20));
                    }

                    // 2. Act
                    tree.WriteToFile(tempFile);

                    // 3. Assert
                    Assert.IsTrue(File.Exists(tempFile), "Export file should exist.");

                    string[] lines = File.ReadAllLines(tempFile);

                    // Check if we got all 20 keys
                    Assert.AreEqual(20, lines.Length, "Missing lines");
                }
            }
            finally
            {
                File.Delete(path);
                File.Delete(tempFile);
            }
        }


        [TestMethod]
        public void GetKeyRangeTest()
        {
            string path = TestHelper.GetTempDb();
            File.Delete(path);

            try
            {
                using (var tree = new BTree(path, pageSize: 256))
                {
                    for (int i = 10; i <= 40; i += 10) tree.Insert(i, Util.NextString(1, 20));

                    // Helper to keep it to one line per test case
                    void VerifyRange(int start, int end, params int[] expected)
                    {
                        var actual = tree.GetKeyRange(start, end);
                        CollectionAssert.AreEqual(expected, actual);
                    }

                    VerifyRange(15, 35, 20, 30);      // Middle
                    VerifyRange(-1, 15, 10);          // Start
                    VerifyRange(38, 60, 40);          // End
                    VerifyRange(-1, 60, 10, 20, 30, 40);   // Full
                    VerifyRange(-20, 0);              // Outside Lower
                    VerifyRange(50, 100);             // Outside Upper
                    VerifyRange(20, 20, 20);          // Single Point
                    VerifyRange(50, 10);              // Inverted
                }
            }
            finally
            {
                File.Delete(path);
            }
        }


        [TestMethod]
        public void GetElementRangeTest()
        {
            string path = TestHelper.GetTempDb();
            File.Delete(path);

            try
            {
                using (var tree = new BTree(path))
                {
                    for (int i = 10; i <= 60; i += 10)
                        tree.Insert(new Element(i, Util.NextString(1, 20)));

                    // LOCAL HELPER.
                    void Verify(int start, int end, params int[] expected)
                    {
                        var actual = tree.GetElementRange(start, end).Select(e => e.Key).ToArray();
                        CollectionAssert.AreEqual(expected, actual, $"Failed for range {start} to {end}");
                    }

                    // Now the calls are incredibly clean
                    Verify(15, 35, 20, 30);
                    Verify(-1, 15, 10);
                    Verify(38, 60, 40, 50, 60);
                    Verify(-1, 60, 10, 20, 30, 40, 50, 60);
                    Verify(-20, 0);
                    Verify(100, 200);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void RangeScanStressTest()
        {
            string path = TestHelper.GetTempDb();
            File.Delete(path);

            try
            {
                using (var tree = new BTree(path))
                {
                    // 1. Setup: Insert 20 keys to force multiple splits and levels
                    // We use a specific gap (5) so we can test "between" values
                    for (int i = 5; i <= 100; i += 5)
                        tree.Insert(i, Util.NextString(1, 20));

                    // Local helper captures 'tree' for clean syntax
                    void Verify(int start, int end, params int[] expected)
                    {
                        var actual = tree.GetKeyRange(start, end);
                        CollectionAssert.AreEqual(expected, actual, $"Failed for {start}-{end}");
                    }

                    // 2. Test: Start, Middle, and End across the leaf nodes
                    Verify(1, 12, 5, 10);                // Start boundary
                    Verify(42, 63, 45, 50, 55, 60);      // Middle bridge (spans nodes)
                    Verify(92, 150, 95, 100);            // End boundary

                    // 3. Delete keys to create holes.
                    tree.Delete(50);
                    tree.Delete(55);

                    // 4. Test the ranges.
                    Verify(42, 63, 45, 60);
                    Verify(71, 74);
                    Verify(48, 62, 60);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

    }
}
