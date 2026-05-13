using UnityEngine;

public static class PoseConverter
{
    public static Matrix4x4 RowMajorToMatrix4x4(float[] rowMajorPose16)
    {
        Matrix4x4 m = Matrix4x4.identity;
        if (rowMajorPose16 == null || rowMajorPose16.Length < 16)
            return m;

        m.m00 = rowMajorPose16[0];
        m.m01 = rowMajorPose16[1];
        m.m02 = rowMajorPose16[2];
        m.m03 = rowMajorPose16[3];
        m.m10 = rowMajorPose16[4];
        m.m11 = rowMajorPose16[5];
        m.m12 = rowMajorPose16[6];
        m.m13 = rowMajorPose16[7];
        m.m20 = rowMajorPose16[8];
        m.m21 = rowMajorPose16[9];
        m.m22 = rowMajorPose16[10];
        m.m23 = rowMajorPose16[11];
        m.m30 = rowMajorPose16[12];
        m.m31 = rowMajorPose16[13];
        m.m32 = rowMajorPose16[14];
        m.m33 = rowMajorPose16[15];
        return m;
    }

    public static bool TryBuildUnityPose(
        float[] rowMajorPose16,
        bool flipCvYToUnity,
        float translationScale,
        bool poseIsInCameraSpace,
        Transform poseReferenceCamera,
        out Vector3 worldPosition,
        out Quaternion worldRotation)
    {
        worldPosition = Vector3.zero;
        worldRotation = Quaternion.identity;
        if (rowMajorPose16 == null || rowMajorPose16.Length < 16)
            return false;

        Matrix4x4 m = RowMajorToMatrix4x4(rowMajorPose16);
        Vector3 localPos = new Vector3(m.m03, m.m13, m.m23) * translationScale;

        // Coordinate frame transform M = diag(1,-1,1) maps OpenCV (X right, Y down, Z forward)
        // to Unity camera (X right, Y up, Z forward).
        // For rotation: R_unity = M * R_cv * M
        //   Col 2 (forward): (R02, -R12, R22)
        //   Col 1 (up):      (-R01,  R11, -R21)
        // For position: negate Y only.
        Vector3 forward, up;
        if (flipCvYToUnity)
        {
            localPos.y = -localPos.y;
            forward = new Vector3(m.m02, -m.m12, m.m22);
            up      = new Vector3(-m.m01, m.m11, -m.m21);
        }
        else
        {
            forward = new Vector3(m.m02, m.m12, m.m22);
            up      = new Vector3(m.m01, m.m11, m.m21);
        }

        if (forward.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f)
            return false;

        Quaternion localRotation = Quaternion.LookRotation(forward.normalized, up.normalized);
        worldPosition = localPos;
        worldRotation = localRotation;
        if (poseIsInCameraSpace && poseReferenceCamera != null)
        {
            worldPosition = poseReferenceCamera.TransformPoint(localPos);
            worldRotation = poseReferenceCamera.rotation * localRotation;
        }

        return true;
    }

    public static bool TryBuildUnityPose(
        float[] rowMajorPose16,
        bool flipCvYToUnity,
        float translationScale,
        bool poseIsInCameraSpace,
        Vector3 poseReferencePosition,
        Quaternion poseReferenceRotation,
        out Vector3 worldPosition,
        out Quaternion worldRotation)
    {
        if (!TryBuildUnityPose(
                rowMajorPose16,
                flipCvYToUnity,
                translationScale,
                false,
                null,
                out Vector3 localPosition,
                out Quaternion localRotation))
        {
            worldPosition = Vector3.zero;
            worldRotation = Quaternion.identity;
            return false;
        }

        if (!poseIsInCameraSpace)
        {
            worldPosition = localPosition;
            worldRotation = localRotation;
            return true;
        }

        worldPosition = poseReferencePosition + poseReferenceRotation * localPosition;
        worldRotation = poseReferenceRotation * localRotation;
        return true;
    }

    /// <summary>
    /// Post-multiplies the 3×3 rotation block of an OpenCV row-major 4×4 pose (<c>R' = R * Q</c>,
    /// with <paramref name="qBodyLocal"/> expressed in mesh/body coordinates). Translation is
    /// unchanged. Use this to align the native tracker's internal body frame with the Unity .obj
    /// mesh frame when both use the same geometry file but differ by a fixed Euler offset —
    /// e.g. yellow seed wireframe matches the scene but tracked pose is systematically ~90° about
    /// local X (<see cref="TrackingOrchestrator"/> applies <c>Q</c> when seeding and inverse when
    /// reading <c>GetTrackedPose</c>).
    /// </summary>
    public static void PostMultiplyBodyRotationRowMajor(float[] rowMajorPose16, Quaternion qBodyLocal)
    {
        if (rowMajorPose16 == null || rowMajorPose16.Length < 16)
            return;

        if (!IsFiniteQuaternion(qBodyLocal))
            return;
        if (Quaternion.Angle(qBodyLocal, Quaternion.identity) < 1e-4f)
            return;

        Matrix4x4 t = RowMajorToMatrix4x4(rowMajorPose16);
        Matrix4x4 r = Matrix4x4.identity;
        r.SetColumn(0, new Vector4(t.m00, t.m10, t.m20, 0f));
        r.SetColumn(1, new Vector4(t.m01, t.m11, t.m21, 0f));
        r.SetColumn(2, new Vector4(t.m02, t.m12, t.m22, 0f));

        Matrix4x4 qMat = Matrix4x4.Rotate(qBodyLocal);
        Matrix4x4 rNew = r * qMat;

        t.m00 = rNew.m00; t.m01 = rNew.m01; t.m02 = rNew.m02;
        t.m10 = rNew.m10; t.m11 = rNew.m11; t.m12 = rNew.m12;
        t.m20 = rNew.m20; t.m21 = rNew.m21; t.m22 = rNew.m22;

        MatrixToRowMajor(t, rowMajorPose16);
    }

    private static void MatrixToRowMajor(Matrix4x4 m, float[] dst)
    {
        dst[0]  = m.m00; dst[1]  = m.m01; dst[2]  = m.m02; dst[3]  = m.m03;
        dst[4]  = m.m10; dst[5]  = m.m11; dst[6]  = m.m12; dst[7]  = m.m13;
        dst[8]  = m.m20; dst[9]  = m.m21; dst[10] = m.m22; dst[11] = m.m23;
        dst[12] = m.m30; dst[13] = m.m31; dst[14] = m.m32; dst[15] = m.m33;
    }

    private static bool IsFiniteQuaternion(Quaternion q)
    {
        return !float.IsNaN(q.x) && !float.IsNaN(q.y) && !float.IsNaN(q.z) && !float.IsNaN(q.w) &&
               !float.IsInfinity(q.x) && !float.IsInfinity(q.y) &&
               !float.IsInfinity(q.z) && !float.IsInfinity(q.w);
    }
}
