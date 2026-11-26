# Benchmark Results

| Keys | Load | Insert | Delete |
| :--- | :--- | :--- | :--- |
| 1K | 20ms | 110ms | 159ms |
| 10K | 66ms | 635ms | 1523ms |
| 100K | 421ms | 8606ms | 15818ms |

## Analysis & Scaling

* **Load:** Scales linearly in O(N) time.
* **Insert and Delete:** Scale at an O(N log N) time.
* **Run Time Hierarchy:** Load < Insert < Delete.
