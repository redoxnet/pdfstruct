// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.ZXing;
using Xunit;
using ZXing;

namespace PdfStruct.Tests;

public class ZXingCodeDecoderTests
{
    [Fact]
    public void MapFormat_QrCode_MapsToQrCodeRole()
    {
        var (role, codeType, text) = ZXingCodeDecoder.MapFormat(BarcodeFormat.QR_CODE, "10-2312079");
        Assert.Equal("qr-code", role);
        Assert.Equal("qr-code", codeType);
        Assert.Equal("10-2312079", text);
    }

    [Theory]
    [InlineData(BarcodeFormat.CODE_39, "code-39")]
    [InlineData(BarcodeFormat.CODE_128, "code-128")]
    [InlineData(BarcodeFormat.DATA_MATRIX, "data-matrix")]
    [InlineData(BarcodeFormat.PDF_417, "pdf-417")]
    public void MapFormat_NonQrFormats_MapToBarcodeRoleWithNormalisedType(BarcodeFormat format, string expectedCodeType)
    {
        var (role, codeType, _) = ZXingCodeDecoder.MapFormat(format, "US011013909B2");
        Assert.Equal("barcode", role);
        Assert.Equal(expectedCodeType, codeType);
    }
}
