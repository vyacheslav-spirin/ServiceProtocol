Service Protocol
=================

## Overview:

The **Service Protocol** - Protocol based on **TCP**, provides messaging of any format, using a request-response approach with minimal overhead.

The protocol arose as a consequence of the need to create a convenient and performance mechanism for interaction between services with minimal latency.

In addition to performance, significant attention is paid to ease of use and error handling.

Working with .NET / Mono 4.5. For building required Visual Studio 2017.

## Features:

 * It can work with messages of any format. For each request / response type, the packing / unpacking code is generated just one time and further used for all data transmission.
 * It supports pipelining. Using one connection, multiple requests can be sent. The protocol tries to pack the maximum amount of data before calling the socket methods.
 * It quickly detects connection problems, sending own keepalive messages.
 * It uses **async / await** approach for receive response from the server.
 * It fully support **Mono**.
 * It contains simple overload control. Drop client connection when the limit of concurrent requests is exceeded on the server. Return error when limit exceeded on the client. This mechanism allows to avoid client and server application hangup during overload.

### Quickstart

#### Install

[ServiceProtocol is available through NuGet](https://www.nuget.org/packages/ServiceProtocol/)

```sh
PM> Install-Package ServiceProtocol
```

#### Set up
First, you need to declare a "schema" for communication between the client and the server. In fact, these are the simple classes inherited from ```ServiceProtocolRequest``` and ```ServiceProtocolResponse```. Classes must be located within the same parent type to correctly generate the packing / unpacking code.

```cs
using System.Text;
using ServiceProtocol;

public static class ExampleServiceProtocolSchema
{
    public class ExampleProcessStringRequest : ServiceProtocolRequest
    {
        public string sourceString;
    }

    public class ExampleProcessStringResponse : ServiceProtocolResponse
    {
        public string processedString;
    }

    public static readonly ServiceProtocolDataContract DataContract = new ServiceProtocolDataContract(typeof(ExampleServiceProtocolSchema), Encoding.UTF8, Encoding.UTF8);
}
```

Pay attention to the line with ```DataContract``` object. The ```ServiceProtocolDataContract``` class generates the code for packing and unpacking the data. This object required for creating clients and servers. It is recommended to cache this object and pass it to all clients and servers who use the same schema, in order not to generate the same code once again.

For the client and server, you need a logger to send errors. Create a simple console logger:

```cs
using System;
using ServiceProtocol;

public class SimpleConsoleErrorLogger : IServiceProtocolErrorLogger
{
    public void Error(string message)
    {
        Console.WriteLine("Service protocol ERROR: " + message);
    }

    public void Fatal(string message)
    {
        Console.WriteLine("Service protocol FATAL: " + message);
    }
}
```

Create client:

```cs
//ServiceProtocolClientManager contains background thread for managing connections.
//Recommended create of one instance of this class for the whole application.
var mgr = new ServiceProtocolClientManager(new SimpleConsoleErrorLogger());

//Arguments: data contract and max concurrent request count
var client = mgr.CreateClient(ExampleServiceProtocolSchema.DataContract, 10000);

client.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 11000));
```
The design of the library implies sending requests and getting response inside asynchronous methods. **Each call** of ```SendRequest``` method **must** be accompanied by **await** keyword. When using **await** keyword, the compiler generates a callback code that calls the methods necessary for the correct client logic work.

```cs
public async void ExampleProcessString()
{
    //custom logic for getting ServiceProtocolClient object. Singleton, client pool, etc.
    var client = ...

    var response = await client.SendRequest<ExampleServiceProtocolSchema.ExampleProcessStringResponse>(new ExampleServiceProtocolSchema.ExampleProcessStringRequest
    {
        sourceString = "example_string"
    });
    
    if(response.HasError)
    {
        Console.WriteLine("Error: " + response.Code);
    }
    else
    {
        Console.WriteLine(response.destinationString);
    }
}
```
Create server:

```cs

//Arguments: data contract, concurrent request count (per client), error logger
var server = new ServiceProtocolServer(ExampleServiceProtocolSchema.DataContract, 10000, new SimpleConsoleErrorLogger());

server.SetHandler<ExampleServiceProtocolSchema.ExampleProcessStringRequest>(
request =>
{
    var processedString = request.sourceString + "_processed";

    if(request.IsWaitResponse)
    {
        var responce = new ExampleServiceProtocolSchema.ExampleProcessStringResponse
        {
            processedString = processedString
        };
        
        request.SendResponse(responce);
    }
});

server.Listen(11000);
```

## **Important!!!**
If the client sends a request and waits for a response, then the server must return the response. Otherwise, the request queue on the client and server will be full and further processing will be impossible. The ```ServiceProtocolRequest``` class provides an auxiliary property ```IsWaitResponse``` to determine whether the client wait a response. The client provides a method ```SendRequestWithoutResponse```, but the main focus in the library is on getting a response from the server. If you do not plan to use method ```SendRequestWithoutResponse```, you can not use the property ```IsWaitResponse``` and always send a response.


### MIT License

Copyright (c) 2018 Vyacheslav Spirin

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.