#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;

public static class RunStateSelfTest
{
    private static int _pass;
    private static int _fail;

    [MenuItem("Tools/Run/Run RunState Tests")]
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        string dir  = Application.persistentDataPath;
        string ship = RunState.FilePath;
        string prog = RunState.ProgressPath;

        // 진짜 세이브 대피
        string shipBak = File.Exists(ship) ? File.ReadAllText(ship) : null;
        string progBak = File.Exists(prog) ? File.ReadAllText(prog) : null;

        void ClearTestFiles()
        {
            if (File.Exists(ship))
                File.Delete(ship);

            if (File.Exists(prog))
                File.Delete(prog);
        }

        try
        {
            // ---------------------------------------------------------
            // 1) 둘 다 없음
            // Exists == false
            // ValidateOrClear 후에도 둘 다 없음
            // ---------------------------------------------------------

            ClearTestFiles();

            Check(!RunState.Exists,
                "둘 다 없으면 Exists == false");

            RunState.ValidateOrClear();

            Check(!File.Exists(ship) && !File.Exists(prog),
                "둘 다 없으면 ValidateOrClear 후에도 없음");


            // ---------------------------------------------------------
            // 2) ship만 있음
            // Exists == false
            // ValidateOrClear 후 ship 삭제
            // ---------------------------------------------------------

            ClearTestFiles();
            File.WriteAllText(ship, "67");

            Check(!RunState.Exists,
                "ship만 있으면 Exists == false");

            RunState.ValidateOrClear();

            Check(!File.Exists(ship) && !File.Exists(prog),
                "ship만 있으면 ValidateOrClear가 반쪽 세이브 삭제");


            // ---------------------------------------------------------
            // 3) progress만 있음
            // Exists == false
            // ValidateOrClear 후 progress 삭제
            // ---------------------------------------------------------

            ClearTestFiles();
            File.WriteAllText(prog, "67");

            Check(!RunState.Exists,
                "progress만 있으면 Exists == false");

            RunState.ValidateOrClear();

            Check(!File.Exists(ship) && !File.Exists(prog),
                "progress만 있으면 ValidateOrClear가 반쪽 세이브 삭제");


            // ---------------------------------------------------------
            // 4) 둘 다 있음
            // Exists == true
            // ValidateOrClear 후 둘 다 그대로
            // ---------------------------------------------------------

            ClearTestFiles();

            File.WriteAllText(ship, "67");
            File.WriteAllText(prog, "76");

            Check(RunState.Exists,
                "둘 다 있으면 Exists == true");

            RunState.ValidateOrClear();

            Check(
                File.Exists(ship) &&
                File.Exists(prog) &&
                File.ReadAllText(ship) == "67" &&
                File.ReadAllText(prog) == "76",
                "완전한 세이브는 ValidateOrClear가 건드리지 않음"
            );


            // ---------------------------------------------------------
            // 5) 정비 플래그 왕복
            // SetPendingFork(refit) → PendingRefit; CommitLane이 같이 지운다.
            // 장 경계(CommitChapter)도 플래그를 들고, ClearPendingRefit이 지운다.
            // ---------------------------------------------------------

            ClearTestFiles();

            RunState.SetPendingFork(0, refit: true);
            Check(RunState.PendingRefit && RunState.PendingLeg == 0,
                "SetPendingFork(refit)는 PendingRefit을 켠다");

            RunState.CommitLane(0, 1);
            Check(!RunState.PendingRefit && RunState.PendingLeg < 0,
                "CommitLane이 pendingLeg와 pendingRefit을 한 번에 지운다");

            RunState.CommitChapter(2, refit: true);
            Check(RunState.PendingRefit && RunState.Sector == 2 && RunState.Leg == 0,
                "CommitChapter(refit)는 장을 넘기면서 정비 플래그를 든다");

            RunState.ClearPendingRefit();
            Check(!RunState.PendingRefit,
                "ClearPendingRefit이 지운다");
        }
        finally
        {
            // 테스트 찌꺼기 제거
            ClearTestFiles();

            // 진짜 세이브 복구
            if (shipBak != null)
                File.WriteAllText(ship, shipBak);

            if (progBak != null)
                File.WriteAllText(prog, progBak);
        }

        Debug.Log($"[RunStateSelfTest] {_pass} pass / {_fail} fail");
    }

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; return; }
        _fail++;
        Debug.LogError($"[RunStateSelfTest] FAIL: {what}");
    }

}
#endif