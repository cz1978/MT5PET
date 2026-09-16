#property copyright "TradePet"
#property version   "1.20"
#property strict
#property description "TradePet bridge for read-only trade observation, chart objects, macro calendar and loss-zone overlays."

input string InpPipeName = "TradePetBridge.v1";
input int InpChartScanMilliseconds = 500;
input int InpHeartbeatMilliseconds = 1000;
input int InpCommandPollMilliseconds = 1000;
input bool InpEconomicCalendar = true;
input int InpCalendarRefreshSeconds = 60;
input bool InpShowLossZones = true;
input color InpLossZoneColor = C'115,35,45';

#define LOSS_ZONE_PREFIX "TradePet::LossZone::"

int g_pipe = INVALID_HANDLE;
ulong g_sequence = 0;
ulong g_started_at = 0;
uint g_last_connect_attempt = 0;
uint g_last_chart_scan = 0;
uint g_last_heartbeat = 0;
uint g_last_command_poll = 0;
uint g_last_calendar_scan = 0;
long g_last_loss_zone_revision = -1;
bool g_trade_dirty = false;
bool g_chart_dirty = true;
bool g_snapshot_sent = false;
bool g_calendar_diagnostic_logged = false;
string g_previous_keys[];
string g_previous_hashes[];
string g_previous_payloads[];

int OnInit()
  {
   g_started_at = GetMicrosecondCount();
   EventSetMillisecondTimer(100);
   return(INIT_SUCCEEDED);
  }

void OnDeinit(const int reason)
  {
   EventKillTimer();
   DeleteManagedLossZones();
   ClosePipe();
  }

void OnTradeTransaction(
   const MqlTradeTransaction &trans,
   const MqlTradeRequest &request,
   const MqlTradeResult &result)
  {
   g_trade_dirty = true;
  }

void OnChartEvent(
   const int id,
   const long &lparam,
   const double &dparam,
   const string &sparam)
  {
   if(id == CHARTEVENT_OBJECT_CREATE || id == CHARTEVENT_OBJECT_CHANGE || id == CHARTEVENT_OBJECT_DELETE)
      g_chart_dirty = true;
  }

void OnTimer()
  {
   uint now = GetTickCount();
   if(!EnsurePipe(now))
      return;

   if(g_trade_dirty)
     {
       if(SendEnvelope("trade_dirty", "{\"reason\":\"transaction\",\"terminalPath\":\"" +
          JsonEscape(TerminalInfoString(TERMINAL_PATH)) + "\"}"))
         g_trade_dirty = false;
     }

   if(g_chart_dirty || now - g_last_chart_scan >= (uint)MathMax(100, InpChartScanMilliseconds))
     {
      ScanCharts();
      g_last_chart_scan = now;
      g_chart_dirty = false;
     }

   if(now - g_last_heartbeat >= (uint)MathMax(500, InpHeartbeatMilliseconds))
     {
      SendHeartbeat();
      g_last_heartbeat = now;
     }

   if(now - g_last_command_poll >= (uint)MathMax(500, InpCommandPollMilliseconds))
     {
      PollLossZoneSnapshot();
      g_last_command_poll = now;
     }

   if(InpEconomicCalendar &&
      now - g_last_calendar_scan >= (uint)(MathMax(15, InpCalendarRefreshSeconds) * 1000))
     {
      SendEconomicCalendarSnapshot();
      g_last_calendar_scan = now;
     }
  }

bool EnsurePipe(const uint now)
  {
   if(g_pipe != INVALID_HANDLE)
      return(true);
   if(now - g_last_connect_attempt < 500)
      return(false);
   g_last_connect_attempt = now;

   string pipe_path = "\\\\.\\pipe\\" + InpPipeName;
   ResetLastError();
   g_pipe = FileOpen(pipe_path, FILE_READ|FILE_WRITE|FILE_BIN);
   if(g_pipe == INVALID_HANDLE)
      return(false);

   SendEnvelope("hello", "{\"bridgeVersion\":\"1.2.0\",\"terminalPath\":\"" +
      JsonEscape(TerminalInfoString(TERMINAL_PATH)) + "\"}");
   g_chart_dirty = true;
   g_snapshot_sent = false;
   return(true);
  }

void ClosePipe()
  {
   if(g_pipe != INVALID_HANDLE)
     {
      FileClose(g_pipe);
      g_pipe = INVALID_HANDLE;
     }
  }

bool SendEnvelope(const string kind,const string payload)
  {
   if(g_pipe == INVALID_HANDLE)
      return(false);

   string server_date = TimeToString(TimeTradeServer(), TIME_DATE);
   StringReplace(server_date, ".", "-");
   string account_key = AccountInfoString(ACCOUNT_SERVER) + "|" +
      IntegerToString((long)AccountInfoInteger(ACCOUNT_LOGIN));
   string source_id = "bridge-" + IntegerToString((long)AccountInfoInteger(ACCOUNT_LOGIN)) + "-" +
      IntegerToString((long)(g_started_at % 1000000000));
   string line = "{\"protocolVersion\":\"1.0\",\"sourceInstanceId\":\"" + JsonEscape(source_id) +
      "\",\"sequence\":" + IntegerToString((long)g_sequence) +
      ",\"occurredAtUtc\":\"" + UtcNowIso() +
      "\",\"accountKey\":\"" + JsonEscape(account_key) +
      "\",\"serverDate\":\"" + server_date +
      "\",\"kind\":\"" + JsonEscape(kind) +
      "\",\"payload\":" + payload + "}\n";

   uchar bytes[];
   int count = StringToCharArray(line, bytes, 0, WHOLE_ARRAY, CP_UTF8);
   if(count > 0 && bytes[count - 1] == 0)
      count--;
   ResetLastError();
   uint written = FileWriteArray(g_pipe, bytes, 0, count);
   FileFlush(g_pipe);
   if((int)written != count)
     {
      ClosePipe();
      return(false);
     }

   g_sequence++;
   return(true);
  }

void SendHeartbeat()
  {
   string payload = "{\"bridgeVersion\":\"1.2.0\",\"terminalPath\":\"" +
      JsonEscape(TerminalInfoString(TERMINAL_PATH)) +
      "\",\"serverTime\":\"" + ServerNowIso() +
      "\",\"serverUtcOffsetSeconds\":" + IntegerToString((long)(TimeTradeServer() - TimeGMT())) +
      ",\"hostChartId\":" + IntegerToString(ChartID()) + "}";
   SendEnvelope("heartbeat", payload);
  }

void SendEconomicCalendarSnapshot()
  {
   datetime server_now = TimeTradeServer();
   datetime server_day = StringToTime(TimeToString(server_now, TIME_DATE));
   MqlCalendarValue values[];
   ResetLastError();
   int total = CalendarValueHistory(values, server_day, server_now + 36 * 60 * 60);
   if(total < 0)
     {
      if(!g_calendar_diagnostic_logged)
        {
         PrintFormat("TradePet calendar query failed: error=%d", GetLastError());
         g_calendar_diagnostic_logged = true;
        }
      return;
     }

   string events = "";
   int included = 0;
   for(int index = 0; index < total; index++)
     {
      MqlCalendarEvent event;
      if(!CalendarEventById(values[index].event_id, event))
         continue;
      if(event.importance != CALENDAR_IMPORTANCE_MODERATE &&
         event.importance != CALENDAR_IMPORTANCE_HIGH)
         continue;

      MqlCalendarCountry country;
      string country_code = "";
      string country_name = "";
      string currency = "";
      if(CalendarCountryById(event.country_id, country))
        {
         country_code = country.code;
         country_name = country.name;
         currency = country.currency;
        }

      if(included > 0)
         events += ",";
      events += "{\"valueId\":" + IntegerToString((long)values[index].id) +
         ",\"eventId\":" + IntegerToString((long)values[index].event_id) +
         ",\"scheduledAtUtc\":\"" + ServerTimeToUtcIso(values[index].time) +
         "\",\"countryCode\":\"" + JsonEscape(country_code) +
         "\",\"countryName\":\"" + JsonEscape(country_name) +
         "\",\"currency\":\"" + JsonEscape(currency) +
         "\",\"name\":\"" + JsonEscape(event.name) +
         "\",\"type\":\"" + CalendarTypeName(event.type) +
         "\",\"importance\":\"" + CalendarImportanceName(event.importance) +
         "\",\"timeMode\":\"" + JsonEscape(EnumToString(event.time_mode)) +
         "\",\"unit\":\"" + JsonEscape(EnumToString(event.unit)) +
         "\",\"multiplier\":\"" + JsonEscape(EnumToString(event.multiplier)) +
         "\",\"digits\":" + IntegerToString((long)event.digits) +
         ",\"previousValue\":" + CalendarNumber(values[index].HasPreviousValue(), values[index].GetPreviousValue(), event.digits) +
         ",\"revisedPreviousValue\":" + CalendarNumber(values[index].HasRevisedValue(), values[index].GetRevisedValue(), event.digits) +
         ",\"forecastValue\":" + CalendarNumber(values[index].HasForecastValue(), values[index].GetForecastValue(), event.digits) +
         ",\"actualValue\":" + CalendarNumber(values[index].HasActualValue(), values[index].GetActualValue(), event.digits) +
         ",\"impact\":\"" + JsonEscape(EnumToString(values[index].impact_type)) +
         "\",\"sourceUrl\":\"" + JsonEscape(event.source_url) +
         "\",\"eventCode\":\"" + JsonEscape(event.event_code) + "\"}";
      included++;
     }

   string payload = "{\"terminalPath\":\"" + JsonEscape(TerminalInfoString(TERMINAL_PATH)) +
      "\",\"serverUtcOffsetSeconds\":" + IntegerToString((long)(TimeTradeServer() - TimeGMT())) +
      ",\"events\":[" + events + "]}";
   SendEnvelope("calendar_snapshot", payload);
   if(!g_calendar_diagnostic_logged)
     {
      PrintFormat("TradePet calendar snapshot sent: source=%d included=%d", total, included);
      g_calendar_diagnostic_logged = true;
     }
  }

string CalendarNumber(const bool has_value,const double value,const uint digits)
  {
   if(!has_value)
      return("null");
   return(DoubleToString(value, (int)MathMin(digits, 8)));
  }

string CalendarImportanceName(const ENUM_CALENDAR_EVENT_IMPORTANCE importance)
  {
   if(importance == CALENDAR_IMPORTANCE_HIGH) return("high");
   if(importance == CALENDAR_IMPORTANCE_MODERATE) return("moderate");
   if(importance == CALENDAR_IMPORTANCE_LOW) return("low");
   return("none");
  }

string CalendarTypeName(const ENUM_CALENDAR_EVENT_TYPE event_type)
  {
   if(event_type == CALENDAR_TYPE_INDICATOR) return("indicator");
   if(event_type == CALENDAR_TYPE_HOLIDAY) return("holiday");
   return("event");
  }

string ServerTimeToUtcIso(const datetime server_time)
  {
   datetime utc_time = server_time - (TimeTradeServer() - TimeGMT());
   string value = TimeToString(utc_time, TIME_DATE|TIME_SECONDS);
   StringReplace(value, ".", "-");
   StringReplace(value, " ", "T");
   return(value + "Z");
  }

void PollLossZoneSnapshot()
  {
   if(!InpShowLossZones)
     {
      if(g_last_loss_zone_revision >= 0)
        {
         DeleteManagedLossZones();
         g_last_loss_zone_revision = -1;
        }
      return;
     }

   string pipe_path = "\\\\.\\pipe\\" + InpPipeName + ".commands";
   ResetLastError();
   int command_pipe = FileOpen(pipe_path, FILE_READ|FILE_BIN);
   if(command_pipe == INVALID_HANDLE)
      return;

   uchar bytes[];
   uint count = FileReadArray(command_pipe, bytes, 0, WHOLE_ARRAY);
   FileClose(command_pipe);
   if(count == 0)
      return;

   string command = CharArrayToString(bytes, 0, (int)count, CP_UTF8);
   ApplyLossZoneSnapshot(command);
  }

void ApplyLossZoneSnapshot(const string command)
  {
   if(JsonStringValue(command, "kind") != "loss_zone_snapshot")
      return;

   string expected_account = JsonStringValue(command, "accountKey");
   string expected_date = JsonStringValue(command, "serverDate");
   string expected_terminal = JsonStringValue(command, "terminalPath");
   string current_date = TimeToString(TimeTradeServer(), TIME_DATE);
   StringReplace(current_date, ".", "-");
   string current_account = AccountInfoString(ACCOUNT_SERVER) + "|" +
      IntegerToString((long)AccountInfoInteger(ACCOUNT_LOGIN));
   if(expected_account != current_account || expected_date != current_date ||
      StringCompare(expected_terminal, TerminalInfoString(TERMINAL_PATH), false) != 0)
      return;

   long revision = JsonLongValue(command, "revision", -1);
   if(revision < 0 || revision == g_last_loss_zone_revision)
      return;

   DeleteManagedLossZones();
   string marker = "\"zones\":[";
   int array_start = StringFind(command, marker);
   int rendered_zones = 0;
   int rendered_charts = 0;
   if(array_start >= 0)
     {
      int position = array_start + StringLen(marker);
      int array_end = StringFind(command, "]", position);
      while(array_end >= 0)
        {
         int object_start = StringFind(command, "{", position);
         if(object_start < 0 || object_start >= array_end)
            break;
         int object_end = StringFind(command, "}", object_start);
         if(object_end < 0 || object_end > array_end)
            break;

         string zone = StringSubstr(command, object_start, object_end - object_start + 1);
         string id = JsonStringValue(zone, "id");
         string symbol = JsonStringValue(zone, "symbol");
         double lower_bound = JsonDoubleValue(zone, "lowerBound", 0.0);
         double center_price = JsonDoubleValue(zone, "centerPrice", 0.0);
         double upper_bound = JsonDoubleValue(zone, "upperBound", 0.0);
         int attempt_count = (int)JsonLongValue(zone, "attemptCount", 0);
         int loss_count = (int)JsonLongValue(zone, "lossCount", 0);
         double cumulative_loss = JsonDoubleValue(zone, "cumulativeLoss", 0.0);
         if(id != "" && symbol != "" && lower_bound < upper_bound)
           {
            rendered_zones++;
            rendered_charts += DrawLossZone(id, symbol, lower_bound, center_price, upper_bound,
               attempt_count, loss_count, cumulative_loss);
           }
         position = object_end + 1;
        }
     }

   g_last_loss_zone_revision = revision;
   PrintFormat("TradePet loss zones rendered: revision=%I64d zones=%d chart_matches=%d",
      revision, rendered_zones, rendered_charts);
  }

int DrawLossZone(
   const string id,
   const string symbol,
   const double lower_bound,
   const double center_price,
   const double upper_bound,
   const int attempt_count,
   const int loss_count,
   const double cumulative_loss)
  {
   int chart_matches = 0;
   long chart_id = ChartFirst();
   while(chart_id >= 0)
     {
      if(ChartSymbol(chart_id) == symbol)
        {
         string base_name = LOSS_ZONE_PREFIX + id;
         string band_name = base_name + "::Band";
         string center_name = base_name + "::Center";
         datetime left_time = D'2000.01.01 00:00';
         datetime right_time = D'2099.12.31 23:59';
         string tooltip = "TradePet 亏损价格带\n" +
            DoubleToString(lower_bound, (int)SymbolInfoInteger(symbol, SYMBOL_DIGITS)) + " — " +
            DoubleToString(upper_bound, (int)SymbolInfoInteger(symbol, SYMBOL_DIGITS)) +
            "\n尝试 " + IntegerToString(attempt_count) + " 次，亏损 " + IntegerToString(loss_count) +
            " 次，累计 " + DoubleToString(cumulative_loss, 2);

         ObjectCreate(chart_id, band_name, OBJ_RECTANGLE, 0,
            left_time, upper_bound, right_time, lower_bound);
         ObjectMove(chart_id, band_name, 0, left_time, upper_bound);
         ObjectMove(chart_id, band_name, 1, right_time, lower_bound);
         ObjectSetInteger(chart_id, band_name, OBJPROP_COLOR, InpLossZoneColor);
         ObjectSetInteger(chart_id, band_name, OBJPROP_STYLE, STYLE_SOLID);
         ObjectSetInteger(chart_id, band_name, OBJPROP_WIDTH, 1);
         ObjectSetInteger(chart_id, band_name, OBJPROP_FILL, true);
         ObjectSetInteger(chart_id, band_name, OBJPROP_BACK, true);
         ObjectSetInteger(chart_id, band_name, OBJPROP_SELECTABLE, false);
         ObjectSetInteger(chart_id, band_name, OBJPROP_SELECTED, false);
         ObjectSetInteger(chart_id, band_name, OBJPROP_HIDDEN, true);
         ObjectSetString(chart_id, band_name, OBJPROP_TOOLTIP, tooltip);

         ObjectCreate(chart_id, center_name, OBJ_HLINE, 0, 0, center_price);
         ObjectSetDouble(chart_id, center_name, OBJPROP_PRICE, center_price);
         ObjectSetInteger(chart_id, center_name, OBJPROP_COLOR, InpLossZoneColor);
         ObjectSetInteger(chart_id, center_name, OBJPROP_STYLE, STYLE_DOT);
         ObjectSetInteger(chart_id, center_name, OBJPROP_WIDTH, 1);
         ObjectSetInteger(chart_id, center_name, OBJPROP_BACK, true);
         ObjectSetInteger(chart_id, center_name, OBJPROP_SELECTABLE, false);
         ObjectSetInteger(chart_id, center_name, OBJPROP_SELECTED, false);
         ObjectSetInteger(chart_id, center_name, OBJPROP_HIDDEN, true);
         ObjectSetString(chart_id, center_name, OBJPROP_TOOLTIP, tooltip + "\n中心价");
         ChartRedraw(chart_id);
         chart_matches++;
        }
      chart_id = ChartNext(chart_id);
     }
   return(chart_matches);
  }

void DeleteManagedLossZones()
  {
   long chart_id = ChartFirst();
   while(chart_id >= 0)
     {
      bool changed = false;
      for(int index = ObjectsTotal(chart_id, -1, -1) - 1; index >= 0; index--)
        {
         string object_name = ObjectName(chart_id, index, -1, -1);
         if(StringFind(object_name, LOSS_ZONE_PREFIX) == 0)
           {
            ObjectDelete(chart_id, object_name);
            changed = true;
           }
        }
      if(changed)
         ChartRedraw(chart_id);
      chart_id = ChartNext(chart_id);
     }
  }

string JsonStringValue(const string json,const string key)
  {
   string marker = "\"" + key + "\":";
   int position = StringFind(json, marker);
   if(position < 0)
      return("");
   position += StringLen(marker);
   while(position < StringLen(json) && StringGetCharacter(json, position) <= 32)
      position++;
   if(position >= StringLen(json) || StringGetCharacter(json, position) != '"')
      return("");
   position++;
   int end = position;
   while(end < StringLen(json) && StringGetCharacter(json, end) != '"')
      end++;
   if(end >= StringLen(json))
      return("");
   string value = StringSubstr(json, position, end - position);
   StringReplace(value, "\\\\", "\\");
   StringReplace(value, "\\r", "\r");
   StringReplace(value, "\\n", "\n");
   StringReplace(value, "\\t", "\t");
   return(value);
  }

long JsonLongValue(const string json,const string key,const long fallback)
  {
   string value = JsonNumberValue(json, key);
   return(value == "" ? fallback : StringToInteger(value));
  }

double JsonDoubleValue(const string json,const string key,const double fallback)
  {
   string value = JsonNumberValue(json, key);
   return(value == "" ? fallback : StringToDouble(value));
  }

string JsonNumberValue(const string json,const string key)
  {
   string marker = "\"" + key + "\":";
   int position = StringFind(json, marker);
   if(position < 0)
      return("");
   position += StringLen(marker);
   while(position < StringLen(json) && StringGetCharacter(json, position) <= 32)
      position++;
   int end = position;
   while(end < StringLen(json))
     {
      ushort character = (ushort)StringGetCharacter(json, end);
      if(character == ',' || character == '}' || character == ']')
         break;
      end++;
     }
   return(StringSubstr(json, position, end - position));
  }

void ScanCharts()
  {
   string current_keys[];
   string current_hashes[];
   string current_payloads[];
   long chart_id = ChartFirst();
   while(chart_id >= 0)
     {
      int total = ObjectsTotal(chart_id, -1, -1);
      for(int index = 0; index < total; index++)
        {
         string object_name = ObjectName(chart_id, index, -1, -1);
         if(object_name == "")
            continue;
         ENUM_OBJECT object_type = (ENUM_OBJECT)ObjectGetInteger(chart_id, object_name, OBJPROP_TYPE);
         if(!IsSupportedObject(object_type))
            continue;
         if((bool)ObjectGetInteger(chart_id, object_name, OBJPROP_HIDDEN))
            continue;

         string payload = BuildObjectPayload(chart_id, object_name, object_type, false);
         string key = BuildObjectKey(chart_id, object_name);
         string hash = IntegerToString((long)StringHash(payload));
         AppendObject(current_keys, current_hashes, current_payloads, key, hash, payload);
         int previous_index = FindString(g_previous_keys, key);
         if(previous_index < 0 || g_previous_hashes[previous_index] != hash)
            SendEnvelope("chart_upsert", payload);
        }
      chart_id = ChartNext(chart_id);
     }

   for(int old_index = 0; old_index < ArraySize(g_previous_keys); old_index++)
     {
      if(FindString(current_keys, g_previous_keys[old_index]) < 0)
        {
         string deleted_payload = g_previous_payloads[old_index];
         if(StringLen(deleted_payload) > 1)
            deleted_payload = StringSubstr(deleted_payload, 0, StringLen(deleted_payload) - 1) + ",\"isDeleted\":true}";
         SendEnvelope("chart_delete", deleted_payload);
        }
     }

   if(!g_snapshot_sent)
     {
       string snapshot = "{\"terminalPath\":\"" + JsonEscape(TerminalInfoString(TERMINAL_PATH)) +
          "\",\"hostChartId\":" + IntegerToString(ChartID()) + ",\"objects\":[";
      for(int item = 0; item < ArraySize(current_payloads); item++)
        {
         if(item > 0)
            snapshot += ",";
         snapshot += current_payloads[item];
        }
      snapshot += "]}";
      if(SendEnvelope("chart_snapshot", snapshot))
         g_snapshot_sent = true;
     }

   ArrayCopy(g_previous_keys, current_keys);
   ArrayCopy(g_previous_hashes, current_hashes);
   ArrayCopy(g_previous_payloads, current_payloads);
  }

string BuildObjectPayload(const long chart_id,const string object_name,const ENUM_OBJECT object_type,const bool deleted)
  {
   string symbol = ChartSymbol(chart_id);
   int digits = (int)SymbolInfoInteger(symbol, SYMBOL_DIGITS);
   string kind = ObjectKindName(object_type);
   string terminal_id = TerminalInfoString(TERMINAL_PATH);
   string payload = "{\"objectKey\":\"" + JsonEscape(BuildObjectKey(chart_id, object_name)) +
      "\",\"terminalId\":\"" + JsonEscape(terminal_id) +
      "\",\"chartId\":" + IntegerToString(chart_id) +
      ",\"objectName\":\"" + JsonEscape(object_name) +
      "\",\"symbol\":\"" + JsonEscape(symbol) +
      "\",\"timeframe\":\"" + JsonEscape(EnumToString((ENUM_TIMEFRAMES)ChartPeriod(chart_id))) +
      "\",\"kind\":\"" + kind +
      "\",\"anchors\":[";

   int points = ObjectPointCount(object_type);
   for(int point = 0; point < points; point++)
     {
      if(point > 0)
         payload += ",";
      datetime anchor_time = (datetime)ObjectGetInteger(chart_id, object_name, OBJPROP_TIME, point);
      double anchor_price = ObjectGetDouble(chart_id, object_name, OBJPROP_PRICE, point);
      payload += "{\"timeEpoch\":" + IntegerToString((long)anchor_time) +
         ",\"price\":" + DoubleToString(anchor_price, digits) + "}";
     }

   string text = "";
   if(object_type == OBJ_TEXT || object_type == OBJ_LABEL)
      text = ObjectGetString(chart_id, object_name, OBJPROP_TEXT);
   long color_value = ObjectGetInteger(chart_id, object_name, OBJPROP_COLOR);
   payload += "],\"text\":\"" + JsonEscape(text) +
      "\",\"colorArgb\":" + IntegerToString(color_value);
   if(deleted)
      payload += ",\"isDeleted\":true";
   payload += "}";
   return(payload);
  }

bool IsSupportedObject(const ENUM_OBJECT object_type)
  {
   return(object_type == OBJ_HLINE || object_type == OBJ_RECTANGLE || object_type == OBJ_TREND ||
      object_type == OBJ_TEXT || object_type == OBJ_LABEL);
  }

string ObjectKindName(const ENUM_OBJECT object_type)
  {
   if(object_type == OBJ_HLINE) return("horizontalLine");
   if(object_type == OBJ_RECTANGLE) return("rectangle");
   if(object_type == OBJ_TREND) return("trendLine");
   if(object_type == OBJ_TEXT) return("text");
   return("label");
  }

int ObjectPointCount(const ENUM_OBJECT object_type)
  {
   if(object_type == OBJ_HLINE) return(1);
   if(object_type == OBJ_RECTANGLE || object_type == OBJ_TREND) return(2);
   if(object_type == OBJ_TEXT) return(1);
   return(0);
  }

string BuildObjectKey(const long chart_id,const string object_name)
  {
   return(TerminalInfoString(TERMINAL_PATH) + "/" + IntegerToString(chart_id) + "/" + object_name);
  }

void AppendObject(string &keys[],string &hashes[],string &payloads[],const string key,const string hash,const string payload)
  {
   int size = ArraySize(keys);
   ArrayResize(keys, size + 1);
   ArrayResize(hashes, size + 1);
   ArrayResize(payloads, size + 1);
   keys[size] = key;
   hashes[size] = hash;
   payloads[size] = payload;
  }

int FindString(const string &values[],const string value)
  {
   for(int index = 0; index < ArraySize(values); index++)
      if(values[index] == value)
         return(index);
   return(-1);
  }

uint StringHash(const string value)
  {
   uint hash = 2166136261;
   for(int index = 0; index < StringLen(value); index++)
     {
      hash ^= StringGetCharacter(value, index);
      hash *= 16777619;
     }
   return(hash);
  }

string JsonEscape(string value)
  {
   StringReplace(value, "\\", "\\\\");
   StringReplace(value, "\"", "\\\"");
   StringReplace(value, "\r", "\\r");
   StringReplace(value, "\n", "\\n");
   StringReplace(value, "\t", "\\t");
   return(value);
  }

string UtcNowIso()
  {
   string value = TimeToString(TimeGMT(), TIME_DATE|TIME_SECONDS);
   StringReplace(value, ".", "-");
   StringReplace(value, " ", "T");
   return(value + "Z");
  }

string ServerNowIso()
  {
   string value = TimeToString(TimeTradeServer(), TIME_DATE|TIME_SECONDS);
   StringReplace(value, ".", "-");
   StringReplace(value, " ", "T");
   return(value);
  }
