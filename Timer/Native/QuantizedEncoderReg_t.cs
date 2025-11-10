/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */
 
using System.Runtime.InteropServices;

// ReSharper disable InconsistentNaming

namespace SurfTimer.Native;

[StructLayout(LayoutKind.Explicit, Pack = 4, Size = 4)]
internal struct QuantizedEncoderReg_t
{
    [FieldOffset(0)]
    public short m_unEncoder;
}
