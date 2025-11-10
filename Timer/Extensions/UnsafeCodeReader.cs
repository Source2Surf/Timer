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
using Iced.Intel;

namespace SurfTimer.Extensions;

internal unsafe class UnsafeCodeReader : CodeReader
{
    private readonly uint  _length;
    private readonly byte* _address;
    private          int   _pos;

    public UnsafeCodeReader(byte* address, uint length)
    {
        _length  = length;
        _address = address;
        _pos     = 0;
    }

    public UnsafeCodeReader(nint address, uint length)
    {
        _length  = length;
        _address = (byte*) address;
        _pos     = 0;
    }

    public bool CanReadByte => _pos < _length;

    public override int ReadByte()
    {
        if (_pos >= _length)
        {
            return -1;
        }

        return *(_address + _pos++);
    }
}