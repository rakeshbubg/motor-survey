"""
Train the damage model (RF-DETR, Apache 2.0) and export it to ONNX for the .NET app.

Run on a GPU: Google Colab (T4 is fine) or a rented GPU. Not on the laptop CPU.
  pip install rfdetr
  python train_rfdetr.py --data dataset --epochs 60

Dataset (COCO format, as exported from CVAT or Roboflow):
  dataset/train/_annotations.coco.json + images
  dataset/valid/_annotations.coco.json + images
  dataset/test/_annotations.coco.json  + images
Classes, in this order (keep it identical in appsettings.json Ai:ClassNames):
  Scratch, Minor dent, Dent, Crack, Broken lamp, Glass damage

Check the rfdetr docs (rfdetr.roboflow.com) for the current API; names below match the 1.x package.
"""
import argparse
from rfdetr import RFDETRMedium

ap = argparse.ArgumentParser()
ap.add_argument("--data", default="dataset")
ap.add_argument("--epochs", type=int, default=60)
ap.add_argument("--batch", type=int, default=8)
ap.add_argument("--out", default="output")
a = ap.parse_args()

model = RFDETRMedium()
model.train(dataset_dir=a.data, epochs=a.epochs, batch_size=a.batch, grad_accum_steps=2, lr=1e-4, output_dir=a.out)
model.export(output_dir=a.out)   # writes an .onnx file in output/: copy it to src/MotorSurvey.Web/models/damage.onnx
print("Done. Copy the exported .onnx to src/MotorSurvey.Web/models/damage.onnx and set Ai:InputSize to the model's resolution.")
