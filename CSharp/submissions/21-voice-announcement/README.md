# Hipcall Anti-Gravity — Tek Tıkla Sesli Mesaj (Announcement API)

Bu proje, Hipcall'un yeni `/api/v3/calls/announcement` uç noktasını kullanarak hedef telefon numarasını arayan ve belirtilen metni **Text-to-Speech (TTS)** ile sesli mesaj / anons olarak okutan bir ASP.NET Core .NET 8 Minimal API uygulamasıdır.

Tıpkı "Click-to-Call" gibi tek tıkla çalışır; ancak iki kişiyi bağlamak yerine doğrudan otomatik sesli bildirim gerçekleştirir.

---

## 📁 Proje Yapısı

```
21-anti-gravity/
├── HipcallAntiGravity.csproj   # .NET 8 Web SDK proje tanımı
├── Program.cs                  # Minimal API endpoint'leri ve IHttpClientFactory mimarisi
├── appsettings.json            # Hipcall BaseUrl, ApiToken, NumberId vb. ayarlar
├── README.md                   # Kurulum ve test kılavuzu
└── wwwroot/
    └── index.html              # Modern, şık ve duyarlı HTML5/CSS3/JavaScript kullanıcı arayüzü
```

---

## ⚙️ Yapılandırma (`appsettings.json`)

`appsettings.json` dosyasını açarak veya ortam değişkenlerini ayarlayarak token bilgilerinizi girin:

```json
{
  "Hipcall": {
    "BaseUrl": "https://use.hipcall.com.tr",
    "ApiToken": "YOUR_HIPCALL_PERSONAL_ACCESS_TOKEN",
    "NumberId": 942,
    "DefaultLanguage": "tr-TR",
    "DefaultRepeat": 1
  }
}
```

Dilerseniz ortam değişkeni olarak da tanımlayabilirsiniz:

```powershell
$env:HIPCALL_API_TOKEN = "BURAYA_TOKEN_YAZIN"
$env:HIPCALL_BASE_URL = "https://use.hipcall.com.tr"
```

---

## 🚀 Çalıştırma

Projeyi derlemek ve çalıştırmak için:

```powershell
dotnet run --project CSharp/submissions/21-anti-gravity/HipcallAntiGravity.csproj
```

Uygulama başladığında tarayıcınızdan açın:
👉 **http://localhost:5000**

---

## 📡 API Uç Noktaları

### 1. `POST /api/send-voice-message`

Ön yüzden gelen veriyi alarak Hipcall Announcement API'sine iletir.

#### İstek Gövdesi:
```json
{
  "calleeNumber": "+90551XXXXXXX",
  "text": "Sayın müşterimiz, siparişiniz kargoya verilmiştir.",
  "language": "tr-TR",
  "repeat": 1,
  "numberId": 942
}
```

#### Hipcall API'sine Gönderilen İstek:
- **URL:** `POST https://use.hipcall.com.tr/api/v3/calls/announcement`
- **Headers:**
  - `Authorization: Bearer <API_TOKEN>`
  - `Content-Type: application/json`
  - `Accept: application/json`
- **Body:**
  ```json
  {
    "callee_number": "+90551XXXXXXX",
    "number_id": 942,
    "repeat": 1,
    "tts": {
      "language": "tr-TR",
      "text": "Sayın müşterimiz, siparişiniz kargoya verilmiştir."
    }
  }
  ```

#### Başarılı Yanıt (HTTP 201 Created):
```json
{
  "success": true,
  "statusCode": 201,
  "callId": "4f003b1c-XXXX-XXXX-XXXX-XXXXXXXXXXXX",
  "message": "Sesli mesaj çağrısı başarıyla oluşturuldu.",
  "calleeNumber": "+90551XXXXXXX",
  "rawResponse": {
    "data": {
      "id": "4f003b1c-XXXX-XXXX-XXXX-XXXXXXXXXXXX"
    }
  }
}
```

---

## 📋 Belgeden Alınan Kritik Kurallar

1. **Repeat Parametresi:** Tekrar sayısı mutlaka `1` ile `5` arasında olmalıdır.
2. **Mesaj Parametreleri:** `tts`, `audio_url` ve `greeting_id` seçeneklerinden sadece biri kullanılmalıdır. Bu projede doğrudan **TTS (Text-to-Speech)** kullanılmıştır.
3. **Telefon Numarası:** `callee_number` E.164 formatında (`+90...`) veya yerel formatta (`90...`) gönderilebilir.

