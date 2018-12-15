using System;
using System.Collections;
using System.Collections.Generic;
using TensorFlow;
using UnityEngine;

class VNectManager {
    private const bool RGB2BGR = true;
    private const int PIXEL_SIZE = 3;
    public int NN_INPUT_WIDTH_MAX = 368;
    public int NN_INPUT_HEIGHT_MAX = 368;
    public int NN_POOL_SIZE = 8;
    public int NN_JOINT_COUNT = 21;
    enum HEATMAP_TYPE { H = 0, X = 1, Y = 2, Z = 3, Length = 4 };

    private Dictionary<string, JointInfo> jointInfos;

    public float[] nnShapeScales;
    public float[] nnInputBuff;

    public int heatmapWidth;
    public int heatmapHeight;
    public IntPtr nnOutputPtr;
    public IntPtr nnOutputPtrX;
    public IntPtr nnOutputPtrY;
    public IntPtr nnOutputPtrZ;
    public float[,,,] heatmapBuff;
    public int[,,] heatmapLabel;
    public int[] heatmapLabelCount;

    public Dictionary<string, Vector2> joint2D = new Dictionary<string, Vector2>();
    public Dictionary<string, Vector3> joint3D = new Dictionary<string, Vector3>();
    public Dictionary<string, bool> extractedJoints = new Dictionary<string, bool>();

    private TFGraph graph;
    private TFSession session;
    private TFShape shape;

    public void Init(Dictionary<string, JointInfo> jointInfos, bool useMultiScale) {
        this.jointInfos = jointInfos;

        nnShapeScales = useMultiScale ? new float[]{ 1.0f, 0.7f } : new float[] { 1.0f };
        nnInputBuff = new float[NN_INPUT_WIDTH_MAX * NN_INPUT_HEIGHT_MAX * PIXEL_SIZE * nnShapeScales.Length];
        heatmapLabelCount = new int[NN_JOINT_COUNT];

        //2Dジョイントの初期値は中央に集めておく
        //TODO：バウンディングボックス周りの処理と相性が良くない？
        foreach (string key in jointInfos.Keys) {
            joint2D[key] = new Vector2(NN_INPUT_WIDTH_MAX / NN_POOL_SIZE / 2, NN_INPUT_HEIGHT_MAX / NN_POOL_SIZE / 2);
            joint3D[key] = new Vector3();
        }

        //VNectのモデルを読み込み
        TextAsset graphModel = Resources.Load("vnect_frozen") as TextAsset;
        graph = new TFGraph();
        graph.Import(graphModel.bytes);
        session = new TFSession(graph);
        shape = new TFShape(nnShapeScales.Length, NN_INPUT_WIDTH_MAX, NN_INPUT_HEIGHT_MAX, PIXEL_SIZE);
    }

    public void Update(Texture2D resizedTexture, float jointDistanceLimit, float jointThreshold, float joint2DLerp, float joint3DLerp, bool useLabeling) {
        Color32[] pixels = resizedTexture.GetPixels32();
        TFTensor inputTensor = CreateShapes(pixels);

        TFSession.Runner runner = session.GetRunner();
        runner.AddInput(graph["Placeholder"][0], inputTensor);
        runner.Fetch(graph["split_2"][0], graph["split_2"][1], graph["split_2"][2], graph["split_2"][3]);

        TFTensor[] outputTensor = runner.Run();
        nnOutputPtr = outputTensor[0].Data;
        nnOutputPtrX = outputTensor[1].Data;
        nnOutputPtrY = outputTensor[2].Data;
        nnOutputPtrZ = outputTensor[3].Data;
        heatmapWidth = (int)outputTensor[0].Shape[1];
        heatmapHeight = (int)outputTensor[0].Shape[2];

        if (heatmapBuff == null){
            heatmapBuff = new float[heatmapHeight, heatmapWidth, NN_JOINT_COUNT, (int)HEATMAP_TYPE.Length];
        }else{
            Array.Clear(heatmapBuff, 0, heatmapBuff.Length);
        }
        ExtractHeatmaps(nnOutputPtr, nnOutputPtrX, nnOutputPtrY, nnOutputPtrZ);
        Extract2DJoint(jointDistanceLimit, jointThreshold, joint2DLerp, useLabeling);
        Extract3DJoint(joint3DLerp);
    }

    private TFTensor CreateShapes(Color32[] pixels) {
        const float ItoF = 1.0f / 255.0f;
        const float MEAN = 0.4f;

        //縮小率の逆数、シェイプの幅と高さの初期化
        float[] invShapeScales = new float[nnShapeScales.Length];
        int[] shapeWidth = new int[nnShapeScales.Length];
        int[] shapeHeight = new int[nnShapeScales.Length];
        for (int i = 0; i < nnShapeScales.Length; ++i) {
            invShapeScales[i] = 1.0f / nnShapeScales[i];
            shapeWidth[i] = (int)(NN_INPUT_WIDTH_MAX * nnShapeScales[i]);
            shapeHeight[i] = (int)(NN_INPUT_HEIGHT_MAX * nnShapeScales[i]);
        }

        Array.Clear(nnInputBuff, 0, nnInputBuff.Length);
        for (int scaleNum = 0; scaleNum < nnShapeScales.Length; ++scaleNum) {
            //縮小した分だけパディングする
            int padHeight = (NN_INPUT_HEIGHT_MAX - shapeHeight[scaleNum]) / 2;
            int padWidth = (NN_INPUT_WIDTH_MAX - shapeWidth[scaleNum]) / 2;

            //scalesの分だけdstの書き込み先をずらす
            int padScale = NN_INPUT_WIDTH_MAX * NN_INPUT_HEIGHT_MAX * scaleNum;

            for (int y = 0; y < shapeHeight[scaleNum]; ++y) {
                //縮小後のdstが基準なのでinvScale倍した位置のsrcから色情報を取得する
                int srcHeight = (int)(y * invShapeScales[scaleNum]);

                for (int x = 0; x < shapeWidth[scaleNum]; ++x) {
                    int srcWidth = (int)(x * invShapeScales[scaleNum]);
                    Color32 src = pixels[srcHeight * NN_INPUT_WIDTH_MAX + srcWidth];

                    //画像の上下を反転
                    int flipHeight = ((NN_INPUT_HEIGHT_MAX - 1) - (padHeight + y)) * NN_INPUT_WIDTH_MAX;

                    int dstPos = (padScale + flipHeight + padWidth + x) * PIXEL_SIZE;
                    if (RGB2BGR) {
                        nnInputBuff[dstPos + 0] = src.b * ItoF - MEAN;
                        nnInputBuff[dstPos + 1] = src.g * ItoF - MEAN;
                        nnInputBuff[dstPos + 2] = src.r * ItoF - MEAN;

                    } else {
                        nnInputBuff[dstPos + 0] = src.r * ItoF - MEAN;
                        nnInputBuff[dstPos + 1] = src.g * ItoF - MEAN;
                        nnInputBuff[dstPos + 2] = src.b * ItoF - MEAN;
                    }
                }
            }
        }

        //バッファからTFTensorを作って返す
        TFTensor tensor = TFTensor.FromBuffer(shape, nnInputBuff, 0, nnInputBuff.Length);
        return tensor;
    }

    //NNからの出力をバッファに取り出す
    //後工程で正規化するのでスケール分は足し込んでいい
    private unsafe void ExtractHeatmaps(IntPtr nnOutputPtr, IntPtr nnOutputPtrX, IntPtr nnOutputPtrY, IntPtr nnOutputPtrZ) {
        Array.Clear(heatmapBuff, 0, heatmapBuff.Length);

        fixed (float* dst = heatmapBuff) {
            float* src = (float*)nnOutputPtr;
            float* srcX = (float*)nnOutputPtrX;
            float* srcY = (float*)nnOutputPtrY;
            float* srcZ = (float*)nnOutputPtrZ;
            for (int scaleNum = 0; scaleNum < nnShapeScales.Length; ++scaleNum) {
                int padHeight = (int)((heatmapHeight - (heatmapHeight * nnShapeScales[scaleNum])) / 2);
                int padWidth = (int)((heatmapWidth - (heatmapWidth * nnShapeScales[scaleNum])) / 2);

                float* dstPos = dst;
                int srcChannel = scaleNum * heatmapHeight * heatmapWidth;
                for (int y = 0; y < heatmapHeight; ++y) {
                    int srcHeight = ((int)(y * nnShapeScales[scaleNum]) + padHeight) * heatmapWidth;

                    for (int x = 0; x < heatmapWidth; ++x) {
                        int srcWidth = ((int)(x * nnShapeScales[scaleNum]) + padWidth);
                        int srcPos = (srcChannel + srcHeight + srcWidth) * NN_JOINT_COUNT;

                        float* _src = src + srcPos;
                        float* _srcX = srcX + srcPos;
                        float* _srcY = srcY + srcPos;
                        float* _srcZ = srcZ + srcPos;
                        for (int j = 0; j < NN_JOINT_COUNT; ++j) {
                            *(dstPos++) += *(_src++);
                            *(dstPos++) += *(_srcX++);
                            *(dstPos++) += *(_srcY++);
                            *(dstPos++) += *(_srcZ++);
                        }
                    }
                }
            }
        }
    }

    //正規化
    private unsafe void NormalizeHeatmap() {
        float[] joint2dMax = new float[NN_JOINT_COUNT];
        float[] joint2dMin = new float[NN_JOINT_COUNT];
        for (int j = 0; j < NN_JOINT_COUNT; ++j) {
            joint2dMin[j] = Mathf.Infinity;
            joint2dMax[j] = -Mathf.Infinity;
        }

        //最小値、最大値
        fixed (float* src = heatmapBuff){
            float* srcPos = src;
            for (int y = 0; y < heatmapHeight; ++y){
                for (int x = 0; x < heatmapWidth; ++x){
                    for (int j = 0; j < NN_JOINT_COUNT; ++j){
                        float v = *srcPos;
                        srcPos += (int)HEATMAP_TYPE.Length;
                        if (joint2dMin[j] > v) { joint2dMin[j] = v; }
                        if (joint2dMax[j] < v) { joint2dMax[j] = v; }
                    }
                }
            }
        }

        //最大値と最小値の差の逆数
        float[] invDiff = new float[NN_JOINT_COUNT];
        for (int j = 0; j < NN_JOINT_COUNT; ++j) {
            invDiff[j] = 1.0f / (joint2dMax[j] - joint2dMin[j]);
        }

        //ジョイントごとに0.0f〜0.1fの範囲に収める
        fixed (float* dst = heatmapBuff){
            float* dstPos = dst;
            for (int y = 0; y < heatmapHeight; ++y){
                for (int x = 0; x < heatmapWidth; ++x){
                    for (int j = 0; j < NN_JOINT_COUNT; ++j){
                        *dstPos -= joint2dMin[j];
                        *dstPos *= invDiff[j];
                        dstPos += (int)HEATMAP_TYPE.Length;
                    }
                }
            }
        }
    }

    private Dictionary<string, Vector2> preFrameJoint2D = new Dictionary<string, Vector2>();
    private Dictionary<string, float> nearestDistance = new Dictionary<string, float>();
    private unsafe void Heatmap2Joint(float distanceLimit, float jointThreshold, float joint2DLerp) {
        preFrameJoint2D.Clear();
        nearestDistance.Clear();
        foreach (string key in jointInfos.Keys) {
            preFrameJoint2D[key] = joint2D[key];
            nearestDistance[key] = Mathf.Infinity;
            extractedJoints[key] = false;
        }

        Vector2 tmp = new Vector2();
        fixed (float*src = heatmapBuff){
            float* srcPos = src;
            for (int y = 0; y < heatmapHeight; ++y){
                for (int x = 0; x < heatmapWidth; ++x){
                    foreach (string key in jointInfos.Keys){
                        float v = *srcPos;
                        srcPos += (int)HEATMAP_TYPE.Length;

                        if (v < jointThreshold) { continue; }

                        float w = x - preFrameJoint2D[key].x;
                        float h = y - preFrameJoint2D[key].y;
                        float distance = w * w + h * h;

                        //他のラベルの方が前回のジョイント位置に近い
                        if (nearestDistance[key] <= distance) { continue; }

                        //前回のジョイント位置から遠いため誤検出とみなす
                        if (distance > distanceLimit) { continue; }

                        nearestDistance[key] = distance;
                        tmp.Set(x, y);
                        joint2D[key] = Vector2.Lerp(preFrameJoint2D[key], tmp, joint2DLerp);
                        extractedJoints[key] = true;
                    }
                }
            }
        }
    }
    //ラベル番号beforをafterに変更
    private unsafe void ModifyLabel(int j, int height, int width, int befor, int after) {
        fixed (int* dst = heatmapLabel){
            int* dstPos = dst + j * height * width;
            for (int y = 0; y < height; ++y){
                for (int x = 0; x < width; ++x){
                    if (*dstPos == befor) { *dstPos = after; }
                    ++dstPos;
                }
            }
        }
    }
    //近傍の最大値
    private int SearchNeighbors(int j, int height, int width, int y, int x) {
        int max = 0;
        bool l = x - 1 >= 0, r = x + 1 < width, t = y - 1 >= 0, b = y + 1 < height;
        if (l && t && (heatmapLabel[j, y - 1, x - 1] > max)) { max = heatmapLabel[j, y - 1, x - 1]; }
        if (t && (heatmapLabel[j, y - 1, x] > max)) { max = heatmapLabel[j, y - 1, x]; }
        if (t && r && (heatmapLabel[j, y - 1, x + 1] > max)) { max = heatmapLabel[j, y - 1, x + 1]; }
        if (l && (heatmapLabel[j, y, x - 1] > max)) { max = heatmapLabel[j, y, x - 1]; }
        if (r && (heatmapLabel[j, y, x + 1] > max)) { max = heatmapLabel[j, y, x + 1]; }
        if (l && b && (heatmapLabel[j, y + 1, x - 1] > max)) { max = heatmapLabel[j, y + 1, x - 1]; }
        if (b && (heatmapLabel[j, y + 1, x] > max)) { max = heatmapLabel[j, y + 1, x]; }
        if (b && r && (heatmapLabel[j, y + 1, x + 1] > max)) { max = heatmapLabel[j, y + 1, x + 1]; }
        return max;
    }
    //ラベリング
    private unsafe void Heatmap2Label(float jointThreshold) {
        if (heatmapLabel == null){
            heatmapLabel = new int[NN_JOINT_COUNT, heatmapHeight, heatmapWidth];
        }else{
            Array.Clear(heatmapLabelCount, 0, heatmapLabelCount.Length);
        }

        int[] counts = new int[NN_JOINT_COUNT];
        fixed (float* src = heatmapBuff){
            float* srcPos = src;
            for (int y = 0; y < heatmapHeight; ++y){
                for (int x = 0; x < heatmapWidth; ++x){
                    for (int j = 0; j < NN_JOINT_COUNT; ++j){
                        float v = *srcPos;
                        srcPos += (int)HEATMAP_TYPE.Length;

                        //ヒートマップが規定値未満（パーツが検出されていない）なら更新しない
                        if (v < jointThreshold) { continue; }
                        //既にラベル番号が振られているなら更新しない
                        if (heatmapLabel[j, y, x] > 0) { continue; }

                        //近傍ラベルの最大値を取得する
                        int max = SearchNeighbors(j, heatmapHeight, heatmapWidth, y, x);
                        //ラベル番号を更新
                        heatmapLabel[j, y, x] = (max == 0) ? ++counts[j] : max;
                    }
                }
            }
        }

        fixed (int* src = heatmapLabel){
            for (int j = 0; j < NN_JOINT_COUNT; ++j){
                if (counts[j] == 0) { continue; }

                //ラベル番号が重複していたら振り直す
                int* srcPos = src + j * heatmapHeight * heatmapWidth;
                for (int y = 0; y < heatmapHeight; ++y){
                    for (int x = 0; x < heatmapWidth; ++x){
                        int num = *(srcPos++);
                        if (num == 0) { continue; }

                        //近傍の最大値が現在値より高い場合は現在値でラベル番号を上書き
                        int max = SearchNeighbors(j, heatmapHeight, heatmapWidth, y, x);
                        if (max > num) { ModifyLabel(j, heatmapHeight, heatmapWidth, max, num); }
                    }
                }

                //振り直しで連番に抜けができたら詰める
                counts[j] = 0;
                srcPos = src + j * heatmapHeight * heatmapWidth;
                for (int y = 0; y < heatmapHeight; ++y){
                    for (int x = 0; x < heatmapWidth; ++x){
                        int num = *(srcPos++);
                        if (num > counts[j]) { ModifyLabel(j, heatmapHeight, heatmapWidth, num, ++counts[j]); }
                    }
                }
                heatmapLabelCount[j] = counts[j];
            }
        }
    }

    //前フレームのジョイントと重心までの距離が最も近いラベルを今フレームのジョイントにする
    private unsafe void Label2Joint(float distanceLimit) {
        foreach (string key in jointInfos.Keys) {
            extractedJoints[key] = false;

            float jointX = joint2D[key].x, jointY = joint2D[key].y;
            int j = jointInfos[key].index;
            float nearestDistance = Mathf.Infinity;

            fixed (int* src = heatmapLabel){
                //ラベル番号は1始まり（0の部分はラベリングされていない）
                for (int num = 1; num <= heatmapLabelCount[j]; ++num){
                    int area = 0;
                    float gravityX = 0.0f, gravityY = 0.0f;

                    int* srcPos = src + j * heatmapHeight * heatmapWidth;
                    for (int y = 0; y < heatmapHeight; ++y){
                        for (int x = 0; x < heatmapWidth; ++x){
                            int v = *(srcPos++);
                            if (num != v) { continue; }

                            ++area;
                            gravityX += x;
                            gravityY += y;
                        }
                    }
                    if (area == 0) { continue; }

                    //ラベルの重心を出す
                    gravityX /= area;
                    gravityY /= area;

                    float w = gravityX - jointX, h = gravityY - jointY;
                    float distance = w * w + h * h;

                    //他のラベルの方が前回のジョイント位置に近い
                    if (nearestDistance <= distance) { continue; }

                    //前回のジョイント位置から遠いため誤検出とみなす
                    if (distance > distanceLimit) { continue; }

                    nearestDistance = distance;
                    joint2D[key] = new Vector2(gravityX, gravityY);
                    extractedJoints[key] = true;
                }
            }
        }
    }
    //検出できなかったジョイントは中央に寄せる
    private void CenteringNonExtracted2DJoint() {
        float centerX = 0, centerY = 0;
        int extractedCount = 0;
        foreach (string key in jointInfos.Keys) {
            if (!extractedJoints[key]) { continue; }
            centerX += joint2D[key].x;
            centerY += joint2D[key].y;
            ++extractedCount;
        }
        centerX /= extractedCount;
        centerY /= extractedCount;

        foreach (string key in jointInfos.Keys) {
            //※検出できたものは更新しないのでtrueの場合にcontinue
            if (extractedJoints[key] == true) { continue; }
            joint2D[key] = new Vector2(centerX, centerX);
        }
    }

    //2Dジョイントのヒートマップ上の位置を取得
    private void Extract2DJoint(float jointDistanceLimit, float jointThreshold, float joint2DLerp, bool useLabeling) {
        float widthLimit = heatmapWidth * jointDistanceLimit;
        float heightLimit = heatmapHeight * jointDistanceLimit;
        float distanceLimit = widthLimit * widthLimit + heightLimit * heightLimit;

        //ヒートマップの正規化
        NormalizeHeatmap();

        if (useLabeling) {
            //ヒートマップをラベリング
            Heatmap2Label(jointThreshold);
            //ラベルからジョイント位置の更新
            Label2Joint(distanceLimit);

        } else {
            //規定値以上で最も前フレームのジョイントに近い位置を選択
            Heatmap2Joint(distanceLimit, jointThreshold, joint2DLerp);
        }
        CenteringNonExtracted2DJoint();
    }

    //3Dジョイントの三次元空間上の位置を取得
    private void Extract3DJoint(float joint3DLerp) {
        float invScaleLen = 1.0f / nnShapeScales.Length;
        foreach (string key in jointInfos.Keys) {
            int _x = (int)joint2D[key].x;
            int _y = (int)joint2D[key].y;
            int _j = jointInfos[key].index;
            float x = heatmapBuff[_y, _x, _j, (int)HEATMAP_TYPE.X] * invScaleLen;
            float y = heatmapBuff[_y, _x, _j, (int)HEATMAP_TYPE.Y] * invScaleLen;
            float z = heatmapBuff[_y, _x, _j, (int)HEATMAP_TYPE.Z] * invScaleLen;
            joint3D[key] = Vector3.Lerp(joint3D[key], new Vector3(-x, -y, -z), joint3DLerp);
        }
    }

    //推定した姿勢を元にバウンディングボックスを更新する
    public void UpdateBoundingBox(ref Rect boundingBox, float videoWidth, float videoHeight) {
        float left = Mathf.Infinity, right = 0.0f;
        float top = Mathf.Infinity, bottom = 0.0f;

        //今フレームの姿勢での、全てのジョイントを囲む矩形を出す
        foreach (string key in jointInfos.Keys) {
            //検出できなかったジョイントは除外する
            if (!extractedJoints[key]) { continue; }

            if (left > joint2D[key].x) { left = joint2D[key].x; }
            if (right < joint2D[key].x) { right = joint2D[key].x; }
            if (top > joint2D[key].y) { top = joint2D[key].y; }
            if (bottom < joint2D[key].y) { bottom = joint2D[key].y; }
        }
        //NNの出力サイズで割って0.0〜1.0に変換
        left /= heatmapWidth;
        right /= heatmapWidth;
        top /= heatmapHeight;
        bottom /= heatmapHeight;
        //前フレームのバウンディングボックスサイズに変換
        left *= boundingBox.width;
        right *= boundingBox.width;
        top *= boundingBox.height;
        bottom *= boundingBox.height;

        //入力映像サイズ（videoWidth * videoHeight）に変換
        left += boundingBox.xMin;
        right += boundingBox.xMin;
        top += boundingBox.yMin;
        bottom += boundingBox.yMin;

        //次フレームで姿勢が変化することを考えて矩形を拡大する
        //矩形の中心と、中心からの幅・高さを出す
        float halfWidth = (right - left) / 2.0f;
        float halfHeight = (bottom - top) / 2.0f;
        float centerX = left + halfWidth;
        float centerY = top + halfHeight;

        //矩形の拡大率は幅・高さの大きい方に合わせる（正方形にしないとNNから検出されない）
        halfWidth = halfWidth * 1.2f;
        halfHeight = halfHeight * 1.1f;
        float half = (halfHeight > halfWidth) ? halfHeight : halfWidth;

        //入力映像サイズの長辺の正方形は超えないようにする
        half = Mathf.Min(half, Mathf.Max(videoWidth, videoHeight) * 0.5f);

        //入力映像サイズの短辺の半分未満にならないようにする
        half = Mathf.Max(half, Mathf.Min(videoWidth, videoHeight) * 0.25f);

        left = centerX - half;
        right = centerX + half;
        top = centerY - half;
        bottom = centerY + half;

        //※バウンディングボックスは正方形
        //※上下左右の端が入力映像サイズを超えているケースがあり得る
        boundingBox.Set(left, top, right - left, bottom - top);
       
    }

};
