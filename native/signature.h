// Copyright (c) Wiesław Šoltés. MIT licensed.
#pragma once
#include <cstdint>
#include <stdexcept>
#include <vector>
#include <span>

namespace dv {
// Bounded ECMA-335 type traversal. Copies original custom modifiers/generic VAR/MVAR
// bytes into the added return local; no managed type resolution or substitution.
class SignatureReader {
    std::span<const uint8_t> bytes_;
public:
    size_t position=0;
    explicit SignatureReader(std::span<const uint8_t> bytes):bytes_(bytes) {if(bytes.size()>65536)throw std::runtime_error("Signature too large");}
    uint8_t Byte(){if(position>=bytes_.size())throw std::runtime_error("Truncated signature");return bytes_[position++];}
    uint8_t Peek(){if(position>=bytes_.size())throw std::runtime_error("Truncated signature");return bytes_[position];}
    uint32_t Number(){auto a=Byte();if(!(a&0x80))return a;if((a&0xc0)==0x80)return ((a&0x3f)<<8)|Byte();
        if((a&0xe0)!=0xc0)throw std::runtime_error("Invalid compressed integer");auto b=Byte(),c=Byte(),d=Byte();return uint32_t(a&0x1f)<<24|uint32_t(b)<<16|uint32_t(c)<<8|d;}
    uint8_t Type(unsigned depth=0) {
        if(depth>32)throw std::runtime_error("Nested signature limit");
        auto type=Byte();while(type==0x1f||type==0x20){Number();type=Byte();}
        switch(type){
            case 0x01:case 0x02:case 0x03:case 0x04:case 0x05:case 0x06:case 0x07:case 0x08:case 0x09:case 0x0a:case 0x0b:
            case 0x0c:case 0x0d:case 0x0e:case 0x16:case 0x18:case 0x19:case 0x1c:return type;
            case 0x11:case 0x12:case 0x13:case 0x1e:Number();return type;
            case 0x0f:case 0x10:case 0x1d:Type(depth+1);return type;
            case 0x15:{auto kind=Byte();if(kind!=0x11&&kind!=0x12)throw std::runtime_error("Invalid generic type");Number();auto n=Number();if(n>256)throw std::runtime_error("Generic argument limit");while(n--)Type(depth+1);return type;}
            case 0x14:{Type(depth+1);auto rank=Number();if(rank>32)throw std::runtime_error("Array rank limit");for(unsigned i=0;i<2;++i){auto n=Number();if(n>rank)throw std::runtime_error("Invalid array bounds");while(n--)Number();}return type;}
            case 0x1b:{auto cc=Byte();if(cc&0x10)Number();auto n=Number();if(n>65535)throw std::runtime_error("Function pointer argument limit");Type(depth+1);while(n--){if(Peek()==0x41)Byte();Type(depth+1);}return type;}
            default:throw std::runtime_error("Unsupported signature type");
        }
    }
    std::vector<uint8_t> ReturnType(bool& isVoid){auto cc=Byte();if((cc&0x0f)!=0)throw std::runtime_error("Only managed default calling convention can be rewritten");if(cc&0x10)Number();Number();const auto start=position;isVoid=Type()==1;return {bytes_.begin()+start,bytes_.begin()+position};}
    static void Encode(std::vector<uint8_t>& out,uint32_t value){if(value<=0x7f)out.push_back(uint8_t(value));else if(value<=0x3fff){out.push_back(uint8_t((value>>8)|0x80));out.push_back(uint8_t(value));}
        else if(value<=0x1fffffff){out.push_back(uint8_t((value>>24)|0xc0));out.push_back(uint8_t(value>>16));out.push_back(uint8_t(value>>8));out.push_back(uint8_t(value));}else throw std::runtime_error("Compressed integer overflow");}
};
}
