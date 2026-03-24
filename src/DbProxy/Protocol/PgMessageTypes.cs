namespace DbProxy.Protocol;

public static class PgMessageTypes
{
    // Protocol versions
    public const int ProtocolVersion30 = 196608; // 3.0
    public const int SslRequestCode = 80877103;
    public const int CancelRequestCode = 80877102;

    // Client message types
    public const byte ClientSimpleQuery = (byte)'Q';
    public const byte ClientParse = (byte)'P';
    public const byte ClientBind = (byte)'B';
    public const byte ClientDescribe = (byte)'D';
    public const byte ClientExecute = (byte)'E';
    public const byte ClientSync = (byte)'S';
    public const byte ClientFlush = (byte)'H';
    public const byte ClientClose = (byte)'C';
    public const byte ClientTerminate = (byte)'X';
    public const byte ClientPassword = (byte)'p';
    public const byte ClientCopyData = (byte)'d';
    public const byte ClientCopyDone = (byte)'c';
    public const byte ClientCopyFail = (byte)'f';

    // Server message types
    public const byte ServerAuth = (byte)'R';
    public const byte ServerBackendKeyData = (byte)'K';
    public const byte ServerParameterStatus = (byte)'S';
    public const byte ServerRowDescription = (byte)'T';
    public const byte ServerDataRow = (byte)'D';
    public const byte ServerCommandComplete = (byte)'C';
    public const byte ServerReadyForQuery = (byte)'Z';
    public const byte ServerErrorResponse = (byte)'E';
    public const byte ServerNoticeResponse = (byte)'N';
    public const byte ServerParseComplete = (byte)'1';
    public const byte ServerBindComplete = (byte)'2';
    public const byte ServerCloseComplete = (byte)'3';
    public const byte ServerNoData = (byte)'n';
    public const byte ServerEmptyQuery = (byte)'I';
    public const byte ServerPortalSuspended = (byte)'s';
    public const byte ServerParameterDescription = (byte)'t';
    public const byte ServerCopyInResponse = (byte)'G';
    public const byte ServerCopyOutResponse = (byte)'H';

    // Auth types
    public const int AuthOk = 0;
    public const int AuthCleartextPassword = 3;
    public const int AuthMd5Password = 5;
    public const int AuthSasl = 10;
    public const int AuthSaslContinue = 11;
    public const int AuthSaslFinal = 12;

    // Transaction status
    public const byte TransactionIdle = (byte)'I';
    public const byte TransactionInBlock = (byte)'T';
    public const byte TransactionFailed = (byte)'E';
}
