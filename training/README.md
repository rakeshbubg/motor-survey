# Training the damage model

1. **Collect** 3,000–5,000 photos from past motor claims (all angles, all lighting, damaged and undamaged cars).
2. **Label** them in CVAT (free, self-hosted) with polygons or boxes for: Scratch, Minor dent, Dent, Crack, Broken lamp, Glass damage.
   Have two surveyors agree a labelling guide first (what counts as minor vs dent) — consistency matters more than volume.
3. **Export** as COCO and split 80 / 10 / 10 into `dataset/train`, `dataset/valid`, `dataset/test`.
4. **Train** on a GPU: `pip install rfdetr && python train_rfdetr.py --data dataset`.
5. **Test** on the held-out test set against what your surveyors found. Target: the model finds at least 90% of real damage.
   Pick `Ai:ScoreThreshold` and `Survey:ReviewConfidence` from these results, not by guessing.
6. **Deploy**: copy the `.onnx` to `src/MotorSurvey.Web/models/damage.onnx`, then build with `-p:UseOnnx=true`.
7. **Improve monthly**: add photos UW corrected during the pilot and retrain.

Optional second model (`models/parts.onnx`): the same steps with car-part labels (bumper, doors, fenders…), so findings name the exact part.

Note: when exported from a COCO dataset, some RF-DETR versions keep class 0 for the dataset's super-category.
Run one test photo and check the class names line up; if they are off by one, add a placeholder name at the start of `Ai:ClassNames`.
