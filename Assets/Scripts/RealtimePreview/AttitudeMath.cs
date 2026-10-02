// Assets/Scripts/RealtimePreview/AttitudeMath.cs
// テレメトリの roll / pitch / yaw [deg] と Unity の Quaternion の相互変換。
//
// テレメトリ側の約束（機体ファームが使う Arduino "Madgwick" ライブラリ 1.2.0 の出力）:
//   Tait-Bryan Z-Y-X。R(機体→地面) = Rz(yaw) * Ry(pitch) * Rx(roll)、右手系・Z が上・単位は度。
//   roll は (-180,180]、pitch は [-90,90]、yaw は +180 された (0,360]。
using System;
using UnityEngine;

/// <summary>テレメトリの角度を Unity のどの軸まわりの回転に割り当てるか（番号は PoseCsvStore.Axis と同じ）。</summary>
public enum AttitudeAxis { X = 0, Y = 1, Z = 2 }

/// <summary>
/// roll / pitch / yaw それぞれの回転軸と、角度に掛ける符号。3つの軸は互いに異なること。
/// 符号は負なら -1、それ以外は +1 として扱う。
/// </summary>
[Serializable]
public struct AttitudeAxisMap
{
    public AttitudeAxis rollAxis;
    public AttitudeAxis pitchAxis;
    public AttitudeAxis yawAxis;
    public int rollSign;
    public int pitchSign;
    public int yawSign;

    public AttitudeAxisMap(AttitudeAxis rollAxis, int rollSign, AttitudeAxis pitchAxis, int pitchSign, AttitudeAxis yawAxis, int yawSign)
    {
        this.rollAxis = rollAxis; this.rollSign = rollSign;
        this.pitchAxis = pitchAxis; this.pitchSign = pitchSign;
        this.yawAxis = yawAxis; this.yawSign = yawSign;
    }

    /// <summary>機体モデル（機首が -Z、上が +Y）に、IMU の +X が「機尾」向き・Z が上で載っている場合。</summary>
    public static AttitudeAxisMap ImuXTowardTail
    {
        get { return new AttitudeAxisMap(AttitudeAxis.Z, -1, AttitudeAxis.X, +1, AttitudeAxis.Y, -1); }
    }

    /// <summary>機体モデル（機首が -Z、上が +Y）に、IMU の +X が「機首」向き・Z が上で載っている場合。</summary>
    public static AttitudeAxisMap ImuXTowardNose
    {
        get { return new AttitudeAxisMap(AttitudeAxis.Z, +1, AttitudeAxis.X, -1, AttitudeAxis.Y, -1); }
    }

    /// <summary>KinematicPreview シーンの PoseCsvStore と同じ割当（roll→X, pitch→Z, yaw→Y で yaw だけ符号反転）。</summary>
    public static AttitudeAxisMap KinematicPreview
    {
        get { return new AttitudeAxisMap(AttitudeAxis.X, +1, AttitudeAxis.Z, +1, AttitudeAxis.Y, -1); }
    }

    /// <summary>(rollAxis, pitchAxis, yawAxis) が (X,Y,Z) の偶置換なら +1、奇置換なら -1、重複があれば 0。</summary>
    public int Parity
    {
        get
        {
            int r = (int)rollAxis, p = (int)pitchAxis, y = (int)yawAxis;
            if (r < 0 || r > 2 || p < 0 || p > 2 || y < 0 || y > 2) return 0;
            if (r == p || p == y || r == y) return 0;
            return ((p - r + 3) % 3 == 1) ? +1 : -1;      // (0,1,2),(1,2,0),(2,0,1) が偶置換
        }
    }

    /// <summary>3つの軸が互いに異なるか。</summary>
    public bool IsValid { get { return Parity != 0; } }

    /// <summary>
    /// 実在する取り付け方（右手系の IMU 座標の軸の付け替え）に対応する割当か。Parity と符号の積が +1 のとき true。
    /// false の場合は、どれか1つの角度が実機と逆向きに動く。
    /// </summary>
    public bool IsRigid
    {
        get { return Parity * AttitudeMath.Sgn(rollSign) * AttitudeMath.Sgn(pitchSign) * AttitudeMath.Sgn(yawSign) > 0; }
    }

    public bool Equals(AttitudeAxisMap o)
    {
        return rollAxis == o.rollAxis && pitchAxis == o.pitchAxis && yawAxis == o.yawAxis &&
               AttitudeMath.Sgn(rollSign) == AttitudeMath.Sgn(o.rollSign) &&
               AttitudeMath.Sgn(pitchSign) == AttitudeMath.Sgn(o.pitchSign) &&
               AttitudeMath.Sgn(yawSign) == AttitudeMath.Sgn(o.yawSign);
    }
}

public static class AttitudeMath
{
    const double Rad2DegD = 180.0 / Math.PI;

    // これより小さいと、float のクォータニオンでは yaw±roll の組み合わせが決まらない（|pitch| が 90° から 1e-4° 以内）
    const double LockEps = 1e-6;

    public static int Sgn(int s) { return s < 0 ? -1 : 1; }

    public static Vector3 AxisVector(AttitudeAxis a)
    {
        switch (a)
        {
            case AttitudeAxis.X: return new Vector3(1f, 0f, 0f);
            case AttitudeAxis.Y: return new Vector3(0f, 1f, 0f);
            default: return new Vector3(0f, 0f, 1f);
        }
    }

    /// <summary>角度を (-180, 180] に折り返す。</summary>
    public static float Wrap180(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        else if (deg <= -180f) deg += 360f;
        return deg;
    }

    /// <summary>
    /// 航空機の定義どおりの合成（外側から yaw → pitch → roll）:
    /// q = AngleAxis(sy*yaw, yawAxis) * AngleAxis(sp*pitch, pitchAxis) * AngleAxis(sr*roll, rollAxis)。
    /// IsRigid な割当であれば、テレメトリの姿勢をそのまま再現する。
    /// </summary>
    public static Quaternion ComposeAerospace(float rollDeg, float pitchDeg, float yawDeg, AttitudeAxisMap m)
    {
        return Quaternion.AngleAxis(Sgn(m.yawSign) * yawDeg, AxisVector(m.yawAxis))
             * Quaternion.AngleAxis(Sgn(m.pitchSign) * pitchDeg, AxisVector(m.pitchAxis))
             * Quaternion.AngleAxis(Sgn(m.rollSign) * rollDeg, AxisVector(m.rollAxis));
    }

    /// <summary>
    /// PoseCsvStore と同じ合成: eul[軸] = 符号 * 角度 として Quaternion.Euler(eul)。
    /// Unity のオイラー角は常に Qy * Qx * Qz の順なので、roll/pitch/yaw の合成順は軸の割当で決まってしまう
    /// （ComposeAerospace と一致するのは yaw→Y, pitch→X, roll→Z のときだけ）。
    /// </summary>
    public static Quaternion ComposeLegacy(float rollDeg, float pitchDeg, float yawDeg, AttitudeAxisMap m)
    {
        Vector3 eul = Vector3.zero;
        eul[(int)m.rollAxis] = Sgn(m.rollSign) * rollDeg;
        eul[(int)m.pitchAxis] = Sgn(m.pitchSign) * pitchDeg;
        eul[(int)m.yawAxis] = Sgn(m.yawSign) * yawDeg;
        return Quaternion.Euler(eul);
    }

    /// <summary>
    /// ComposeAerospace の逆変換。roll, yaw は (-180,180]、pitch は [-90,90] の度で返す。
    /// 6通りの軸割当と全ての符号で成り立つ。pitch = ±90（ジンバルロック）では roll を 0 とし、残りを yaw に寄せる
    /// （返した3つの角度を合成し直せば、必ず元の回転に戻る）。
    /// </summary>
    public static Vector3 DecomposeAerospace(Quaternion q, AttitudeAxisMap m)
    {
        int eps = m.Parity;
        if (eps == 0) return Vector3.zero;
        Vector3 std = DecomposeZyx(q[(int)m.rollAxis], q[(int)m.pitchAxis], q[(int)m.yawAxis], q.w, eps);
        // std = (PHI, THETA, PSI) = eps * (sr*roll, sp*pitch, sy*yaw)
        float roll = Wrap180(eps * Sgn(m.rollSign) * std.x);
        float pitch = eps * Sgn(m.pitchSign) * std.y;
        float yaw = Wrap180(eps * Sgn(m.yawSign) * std.z);
        return new Vector3(roll + 0f, pitch + 0f, yaw + 0f);      // "+ 0f" は -0 を +0 にするため
    }

    /// <summary>
    /// ComposeLegacy の逆変換。Unity の X 軸に割り当てた角度が [-90,90] に制限される
    /// （KinematicPreview の割当では roll = ±90 がジンバルロックになる）。
    /// </summary>
    public static Vector3 DecomposeLegacy(Quaternion q, AttitudeAxisMap m)
    {
        if (m.Parity == 0) return Vector3.zero;
        // Quaternion.Euler は Qy * Qx * Qz: 外側 Y・中間 X・内側 Z（偶置換）
        Vector3 std = DecomposeZyx(q.z, q.x, q.y, q.w, +1);       // (ez, ex, ey)
        Vector3 e = new Vector3(std.y, std.z, std.x);             // Unity のオイラー角 (x, y, z)、±180
        float roll = Wrap180(Sgn(m.rollSign) * e[(int)m.rollAxis]);
        float pitch = Wrap180(Sgn(m.pitchSign) * e[(int)m.pitchAxis]);
        float yaw = Wrap180(Sgn(m.yawSign) * e[(int)m.yawAxis]);
        return new Vector3(roll + 0f, pitch + 0f, yaw + 0f);
    }

    // 単位クォータニオン (eps*vx, eps*vy, eps*vz, w) = qz(PSI) * qy(THETA) * qx(PHI) の Z-Y-X オイラー角。
    // 半角の形で解くので、ジンバルロックの近くでも精度が落ちない:
    //   w + y = k+ cos((PSI-PHI)/2), z - x = k+ sin((PSI-PHI)/2), k+ = sqrt(1 + sin THETA)
    //   w - y = k- cos((PSI+PHI)/2), x + z = k- sin((PSI+PHI)/2), k- = sqrt(1 - sin THETA)
    // 戻り値は (PHI, THETA, PSI) [deg]（PSI は ±180 の外に出ることがあるので、呼び出し側で折り返す）。
    static Vector3 DecomposeZyx(double vx, double vy, double vz, double vw, int eps)
    {
        double n = Math.Sqrt(vx * vx + vy * vy + vz * vz + vw * vw);
        if (!(n > 1e-12)) return Vector3.zero;                    // default(Quaternion) や NaN
        double s = eps / n;
        double x = vx * s, y = vy * s, z = vz * s, w = vw / n;
        double a = w + y, b = z - x, c = w - y, d = x + z;
        double kp = Math.Sqrt(a * a + b * b);
        double km = Math.Sqrt(c * c + d * d);
        double theta = 2.0 * Math.Atan2(kp, km) - 0.5 * Math.PI;
        double phi, psi;
        if (km < LockEps)            // THETA = +90: PSI - PHI しか決まらない
        {
            phi = 0.0;
            psi = 2.0 * Math.Atan2(b, a);
        }
        else if (kp < LockEps)       // THETA = -90: PSI + PHI しか決まらない
        {
            phi = 0.0;
            psi = 2.0 * Math.Atan2(d, c);
        }
        else
        {
            double hs = Math.Atan2(d, c);     // (PSI + PHI) / 2
            double hd = Math.Atan2(b, a);     // (PSI - PHI) / 2
            psi = hs + hd;
            phi = hs - hd;
        }
        return new Vector3((float)(phi * Rad2DegD), (float)(theta * Rad2DegD), (float)(psi * Rad2DegD));
    }
}
