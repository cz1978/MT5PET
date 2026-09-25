#property strict
#property description "TradePet read-only account and position monitor. No trading or DLL access."

string instance_id;
long sequence = 0;
datetime last_server_time = 0;
int server_offset = 0;
bool have_offset = false;
int last_login = 0;
string last_server = "";

string Quote(string value)
{
   StringReplace(value, "\\", "\\\\");
   StringReplace(value, "\"", "\\\"");
   StringReplace(value, "\r", "\\r");
   StringReplace(value, "\n", "\\n");
   StringReplace(value, "\t", "\\t");
   return "\"" + value + "\"";
}

string Number(double value) { return DoubleToString(value, 8); }
string Utc(datetime value)
{
   string date = TimeToString(value, TIME_DATE);
   StringReplace(date, ".", "-");
   return Quote(date + "T" + TimeToString(value, TIME_SECONDS) + "Z");
}

int OnInit()
{
   if(IsTesting()) return INIT_FAILED;
   instance_id = "mt4-ea-" + IntegerToString(ChartID()) + "-" + IntegerToString((long)TimeLocal()) + "-" + IntegerToString(GetTickCount());
   EventSetTimer(1);
   OnTimer();
   return INIT_SUCCEEDED;
}

void OnDeinit(const int reason) { EventKillTimer(); }

void OnTimer()
{
   datetime utc = TimeGMT();
   if(last_login != AccountNumber() || last_server != AccountServer())
   {
      last_login = AccountNumber();
      last_server = AccountServer();
      last_server_time = 0;
      have_offset = false;
   }
   // Round away quote latency; MT4 exposes server timestamps rather than UTC order timestamps.
   datetime server_time = TimeCurrent();
   // Wait for an observed server tick; on quiet weekends TimeCurrent may be days old.
   if(last_server_time != 0 && server_time != last_server_time)
   {
      int candidate = (int)MathRound((double)(server_time - utc) / 900.0) * 900;
      if(MathAbs(candidate) <= 14 * 3600 && MathAbs((double)(server_time - utc - candidate)) < 120)
      {
         server_offset = candidate;
         have_offset = true;
      }
   }
   last_server_time = server_time;
   int offset = server_offset;
   string positions = "", orders = "", specs = "";
   int total = OrdersTotal();
   for(int index = 0; index < total; index++)
   {
      if(!OrderSelect(index, SELECT_BY_POS, MODE_TRADES)) return;
      string symbol = OrderSymbol();
      int kind = OrderType();
      string ticket = IntegerToString(OrderTicket());
      if(kind == OP_BUY || kind == OP_SELL)
      {
         if(StringLen(positions) > 0) positions += ",";
         positions += "{\"ticket\":" + ticket + ",\"positionId\":" + ticket
            + ",\"symbol\":" + Quote(symbol) + ",\"side\":" + Quote(kind == OP_BUY ? "buy" : "sell")
            + ",\"volume\":" + Number(OrderLots()) + ",\"entryPrice\":" + Number(OrderOpenPrice())
            + ",\"currentPrice\":" + Number(MarketInfo(symbol, kind == OP_BUY ? MODE_BID : MODE_ASK))
            + ",\"profit\":" + Number(OrderProfit()) + ",\"swap\":" + Number(OrderSwap())
            + ",\"stopLoss\":" + Number(OrderStopLoss()) + ",\"takeProfit\":" + Number(OrderTakeProfit())
            + ",\"openedAtUtc\":" + Utc(OrderOpenTime() - offset) + "}";
      }
      else if(kind >= OP_BUYLIMIT && kind <= OP_SELLSTOP)
      {
         if(StringLen(orders) > 0) orders += ",";
         string types[4] = {"buy_limit", "sell_limit", "buy_stop", "sell_stop"};
         orders += "{\"ticket\":" + ticket + ",\"symbol\":" + Quote(symbol)
            + ",\"type\":" + Quote(types[kind - OP_BUYLIMIT]) + ",\"volume\":" + Number(OrderLots())
            + ",\"price\":" + Number(OrderOpenPrice()) + ",\"stopLoss\":" + Number(OrderStopLoss())
            + ",\"takeProfit\":" + Number(OrderTakeProfit()) + ",\"createdAtUtc\":" + Utc(OrderOpenTime() - offset) + "}";
      }
      if(StringLen(specs) > 0) specs += ",";
      double point = MarketInfo(symbol, MODE_POINT);
      specs += "{\"symbol\":" + Quote(symbol) + ",\"point\":" + Number(point)
         + ",\"tickSize\":" + Number(MarketInfo(symbol, MODE_TICKSIZE) * point)
         + ",\"digits\":" + IntegerToString((int)MarketInfo(symbol, MODE_DIGITS)) + "}";
   }
   if(OrdersTotal() != total) return;
   string json = "{\"version\":1,\"platform\":\"mt4\",\"sourceInstanceId\":" + Quote(instance_id)
      + ",\"sequence\":" + IntegerToString(++sequence) + ",\"terminalPath\":" + Quote(TerminalInfoString(TERMINAL_PATH))
      + ",\"connected\":" + (IsConnected() ? "true" : "false") + ",\"capturedAtUtc\":" + Utc(utc)
      + ",\"account\":{\"server\":" + Quote(AccountServer()) + ",\"login\":" + IntegerToString(AccountNumber())
      + ",\"currency\":" + Quote(AccountCurrency()) + "},\"balance\":" + Number(AccountBalance())
      + ",\"equity\":" + Number(AccountEquity()) + ",\"floatingPnl\":" + Number(AccountProfit())
      + (have_offset ? ",\"serverUtcOffsetSeconds\":" + IntegerToString(offset) : "")
      + ",\"positions\":[" + positions + "],\"orders\":[" + orders + "],\"symbolSpecifications\":[" + specs + "]}";
   int file = FileOpen("TradePet\\snapshot.tmp", FILE_WRITE | FILE_TXT | FILE_ANSI, 0, CP_UTF8);
   if(file == INVALID_HANDLE) return;
   FileWriteString(file, json);
   FileFlush(file);
   FileClose(file);
   FileMove("TradePet\\snapshot.tmp", 0, "TradePet\\snapshot.json", FILE_REWRITE);
}
