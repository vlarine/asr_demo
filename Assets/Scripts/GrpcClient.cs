using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

using Grpc.Core;
using Smartspeech.Recognition.V1;

public class GrpcClient : MonoBehaviour
{
    public string authData = "<Your Base64 encoded credentials>";
    public string RqUID = "<Your RqUID>";
    public string normalizedText = "";
    public string text = "";
    private string _host = "smartspeech.sber.ru";
    private string _authURL = "https://salute.online.sberbank.ru:9443/api/v2/oauth";
    private string _postFileUrl = "https://smartspeech.sber.ru/rest/v1/speech:recognize";

    private string testFilename = "auto16";

    private int _defaultBufferSize = 2048; // Used in SendTestData

    private int _defaultBufferSampleSize = 1600; // _sampleRate * _microphoneBufferInSec MUST be devided 
                                                 // by _defaultBufferSampleSize!!!
    
    private int _sampleRate = 16000;

    private int _microphoneBufferInSec = 10;
    private int _microphoneDelay = 50; // Delay for asking for the Microphone position

    private RecognitionOptions _options;
    private Channel _channel;
    private SmartSpeech.SmartSpeechClient _client;
    private bool _eou = false; // End of utterance. Set by recognizer.
    private bool _hasMicrophone = false;


    private string _token;
    private long _expiresAt;
    void Start()
    {
        Debug.Log("GrpcClient Start");
        StartCoroutine(MicrophoneOn());
        StartCoroutine(SetToken());
        InitOptions();
    }

    void Update()
    {
        if (_eou && _client != null && _hasMicrophone)
        {
            Debug.Log("Ready to send");
            _eou = false;
            //StartCoroutine(SendTestFile());
            //SendTestData();
            SendMicData();
        }
    }

    void InitClient()
    {
        InitAuthenticatedChannel(_host);
        _client = new SmartSpeech.SmartSpeechClient(_channel);
    }

    void InitAuthenticatedChannel(string address)
    {
        var credentials = CallCredentials.FromInterceptor((context, metadata) =>
        {
            if (!string.IsNullOrEmpty(_token))
            {
                metadata.Add("Authorization", $"Bearer {_token}");
            }
            return Task.CompletedTask;
        });

        _channel = new Channel(address, ChannelCredentials.Create(new SslCredentials(), credentials));
    }

    void InitOptions()
    {
        _options = new RecognitionOptions();
        _options.SampleRate = _sampleRate;
        _options.AudioEncoding = RecognitionOptions.Types.AudioEncoding.PcmS16Le;
        _options.EnablePartialResults = true;
    }

    IEnumerator MicrophoneOn()
    {
        findMicrophones();

        yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
        if (Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            Debug.Log("Microphone found");
            _hasMicrophone = true;
        }
        else
        {
            Debug.Log("Microphone not found");
        }
    }

    void findMicrophones()
    {
        foreach (var device in Microphone.devices)
        {
            Debug.Log("Name: " + device);
        }
    }

    byte[] LoadTestFile()
    {
        TextAsset bindata = Resources.Load(testFilename) as TextAsset;
        return bindata.bytes;
    }

    public static byte[] GetSamplesWaveData(float[] samples, int samplesCount)
    {
        var pcm = new byte[samplesCount * 2];
        int sampleIndex = 0,
        pcmIndex = 0;

        while (sampleIndex < samplesCount)
        {
            var outsample = (short)(samples[sampleIndex] * short.MaxValue);
            pcm[pcmIndex] = (byte)(outsample & 0xff);
            pcm[pcmIndex + 1] = (byte)((outsample >> 8) & 0xff);

            sampleIndex++;
            pcmIndex += 2;
        }

        return pcm;
    }

    IEnumerator SetToken()
    {
        WWWForm form = new WWWForm();
        form.AddField("scope", "SBER_SPEECH");


        using (UnityWebRequest www = UnityWebRequest.Post(_authURL, form))
        {
            www.SetRequestHeader("Authorization", "Basic " + authData);
            www.SetRequestHeader("RqUID", RqUID);
            www.SetRequestHeader("Content-Type", "application/x-www-form-urlencoded");
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.Log(www.error);
                Debug.Log(www.downloadHandler.text);
            }
            else
            {
                Debug.Log("Auth request complete: " + www.downloadHandler.text);
                var authInfo = AuthInfo.CreateFromJSON(www.downloadHandler.text);
                _token = authInfo.access_token;
                _eou = true;
                _expiresAt = authInfo.expires_at;
                Debug.Log("Token:" + _token);
                InitClient();
            }
        }
    }

    async void SendMicData()
    {
        bool eou = _eou;
        string deviceName = Microphone.devices[0];
        AudioSource audioSource = GetComponent<AudioSource>();
        audioSource.clip = Microphone.Start(deviceName, true, _microphoneBufferInSec, _sampleRate);

        Debug.Log("Cannels: " + audioSource.clip.channels);
        Debug.Log("Frequency: " + audioSource.clip.frequency);
        Debug.Log("Length: " + audioSource.clip.length);
        Debug.Log("LoadType: " + audioSource.clip.loadType);

        using (var call = _client.Recognize())
        {
            var responseReaderTask = Task.Run(async () =>
            {
                while (await call.ResponseStream.MoveNext())
                {
                    var response = call.ResponseStream.Current;
                    Debug.Log("Received " + response);
                    eou = response.Eou;
                    var results = response.Results;
                    text = results[0].Text;
                    normalizedText = results[0].NormalizedText;
                    Debug.Log("Text: " + text);
                    Debug.Log("NormalizedText: " + normalizedText);
                }
            });

            int bufferSize = _defaultBufferSampleSize;
            float[] samples = new float[bufferSize];
            byte[] buffer = new byte[bufferSize * 2];
            int curDataIndex = 0;
            int microphonePosition = 0;
            var streamRequest = new RecognitionRequest();

            streamRequest.Options = _options;
            await call.RequestStream.WriteAsync(streamRequest);

            while (! eou)
            {
                microphonePosition = Microphone.GetPosition(deviceName);
                if ((curDataIndex + bufferSize == audioSource.clip.samples && microphonePosition < curDataIndex) || 
                    (curDataIndex + bufferSize <= microphonePosition))
                {
                    audioSource.clip.GetData(samples, curDataIndex);
                    curDataIndex += bufferSize;
                    if (curDataIndex >= audioSource.clip.samples)
                         curDataIndex = 0;

                    buffer = GetSamplesWaveData(samples, bufferSize);
 
                    streamRequest.AudioChunk = Google.Protobuf.ByteString.CopyFrom(buffer);
                    await call.RequestStream.WriteAsync(streamRequest);
                }
                else
                {
                    await Task.Delay(_microphoneDelay);
                }
            }
            
            await call.RequestStream.CompleteAsync();
            await responseReaderTask;
        }


        //_channel.ShutdownAsync().Wait();
        Microphone.End(deviceName);
        _eou = eou;
    }

    async void SendTestData()
    {
        byte[] data = LoadTestFile();
        Debug.Log("datasize=" + data.Length);

        using (var call = _client.Recognize())
        {
            var responseReaderTask = Task.Run(async () =>
            {
                while (await call.ResponseStream.MoveNext())
                {
                    var response = call.ResponseStream.Current;
                    Debug.Log("Received " + response);
                    var eou = response.Eou;
                    var results = response.Results;
                    var text = results[0].Text;
                    normalizedText = results[0].NormalizedText;
                    Debug.Log("Text: " + text);
                    Debug.Log("NormalizedText: " + normalizedText);
                }
            });

            int bufferSize = _defaultBufferSize;
            int curDataIndex = 0;
            var streamRequest = new RecognitionRequest();

            streamRequest.Options = _options;
            await call.RequestStream.WriteAsync(streamRequest);

            while (curDataIndex < data.Length-1)
            {
                int remaining = data.Length - curDataIndex;
                if (remaining < bufferSize)
                    bufferSize = remaining;
 
                byte[] buffer = new byte[bufferSize];
                System.Array.Copy(data, curDataIndex, buffer, 0, bufferSize);
 
                streamRequest.AudioChunk = Google.Protobuf.ByteString.CopyFrom(buffer);
                await call.RequestStream.WriteAsync(streamRequest);

                curDataIndex += bufferSize;
                Debug.Log("Sent " + bufferSize);
            }

            await call.RequestStream.CompleteAsync();
            await responseReaderTask;
        }

        _channel.ShutdownAsync().Wait();
    }

    IEnumerator SendTestFile()
    /*
        curl -X POST \
            -H "Authorization: Bearer {{token}}" \
            -H "Content-Type: audio/x-pcm;bit=16;rate=16000" \
            --data-binary @./audio.pcm \ https://smartspeech.sber.ru/rest/v1/speech:recognize
    */
    {
        byte[] data = LoadTestFile();

        WWWForm form = new WWWForm();
        form.AddBinaryData("", data);

        using (UnityWebRequest www = UnityWebRequest.Post(_postFileUrl, form))
        {
            www.SetRequestHeader("Authorization", "Bearer " + _token);
            www.SetRequestHeader("Content-Type", "audio/x-pcm;bit=16;rate=16000");
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.Log(www.error);
                Debug.Log(www.downloadHandler.text);
            }
            else
            {
                Debug.Log("Request complete: " + www.downloadHandler.text);
            }
        }
    }
    
}

[System.Serializable]
public class AuthInfo
{
    public string access_token;
    public long expires_at;

    public static AuthInfo CreateFromJSON(string jsonString)
    {
        return JsonUtility.FromJson<AuthInfo>(jsonString);
    }
}
