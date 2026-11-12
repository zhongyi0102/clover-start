// Copyright 2021 Google LLC
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// https://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// Adapted for CloverStart / .NET Framework: only the scalar color conversion
// functions from material-foundation/material-color-utilities
// typescript/utils/color_utils.ts. This is not an HCT/Material You port.
using System;

namespace Clover
{
    static class MaterialColorMath
    {
        public static double Linearized(double component)
        {
            double n = Math.Max(0, Math.Min(255, component)) / 255.0;
            return (n <= 0.040449936 ? n / 12.92 : Math.Pow((n + 0.055) / 1.055, 2.4)) * 100;
        }

        public static byte Delinearized(double component)
        {
            double n = Math.Max(0, Math.Min(100, component)) / 100;
            double d = n <= 0.0031308 ? n * 12.92 : 1.055 * Math.Pow(n, 1.0 / 2.4) - 0.055;
            return (byte)Math.Max(0, Math.Min(255, Math.Round(d * 255)));
        }

        public static double YFromLstar(double tone)
        {
            double f = (Math.Max(0, Math.Min(100, tone)) + 16) / 116;
            double f3 = f * f * f;
            return 100 * (f3 > 216.0 / 24389 ? f3 : (116 * f - 16) / (24389.0 / 27));
        }

        public static double LstarFromY(double y)
        {
            double t = Math.Max(0, Math.Min(100, y)) / 100;
            double f = t > 216.0 / 24389 ? Math.Pow(t, 1.0 / 3) : ((24389.0 / 27) * t + 16) / 116;
            return f * 116 - 16;
        }
    }
}
